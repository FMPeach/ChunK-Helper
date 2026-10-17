using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PureKBridge.App.Services.NativePublishing;

[ComVisible(false)]
public abstract class HostedServiceState : INativeUpnpEventSource
{
    internal Func<string, object?[], object?[]> InvokeAction { get; set; } = null!;
    internal Func<string, object> ReadVariable { get; set; } = null!;
    internal Action<Action> Post { get; set; } = action => action();
    internal Action<Exception>? EventFailed { get; set; }
    private INativeUpnpEventSink? _sink;
    internal bool HasSubscriber => _sink is not null;
    private bool _closed;
    public object?[] Execute(string action, object?[] arguments) => !_closed ? InvokeAction(action, arguments) :
        throw new COMException("设备已断开。", unchecked((int)0x80004005));
    public object Read(string variable) => ReadVariable(variable);
    public void Advise(object sink) => _sink = (INativeUpnpEventSink)sink;
    public void Unadvise(object sink) { if (ReferenceEquals(_sink, sink)) _sink = null; }
    internal void Changed(IEnumerable<int> dispIds)
    {
        var values = dispIds.Distinct().ToArray();
        if (values.Length == 0) return;
        Post(() =>
        {
            if (_closed || _sink is null) return;
            try { _sink.OnStateChanged((uint)values.Length, values); }
            catch (Exception error) { EventFailed?.Invoke(error); }
        });
    }
    internal void Close() { _closed = true; _sink = null; }
}

internal static class ServiceDispatchFactory
{
    private static readonly Dictionary<string, Type> Cache = new();
    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.Ordinal);
    static ServiceDispatchFactory()
    {
        // ComDefaultInterface's serialized Type attribute is resolved by assembly name.
        // Dynamic schema assemblies exist only in memory, never as DLLs on disk.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            lock (Cache) return Assemblies.GetValueOrDefault(new AssemblyName(args.Name).Name ?? "");
        };
    }
    public static HostedServiceState Create(ServiceSchema schema, Func<string, object?[], object?[]> invoke, Func<string, object> read)
    {
        Type type;
        lock (Cache)
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema.Xml)));
            if (!Cache.TryGetValue(fingerprint, out type!))
            {
                if (Cache.Count >= 64) throw new InvalidOperationException("本次会话的服务定义过多，请重启 Helper。");
                Cache.Add(fingerprint, type = Build(schema, fingerprint));
            }
        }
        var state = (HostedServiceState)Activator.CreateInstance(type)!;
        state.InvokeAction = invoke; state.ReadVariable = read;
        return state;
    }

    private static Type Build(ServiceSchema schema, string fingerprint)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ChunKDispatch_" + fingerprint[..16]), AssemblyBuilderAccess.Run);
        Assemblies.Add(assembly.GetName().Name!, assembly);
        var module = assembly.DefineDynamicModule("Services");
        var iface = module.DefineType("IService_" + fingerprint[..16], TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        Attribute(iface.SetCustomAttribute, typeof(ComVisibleAttribute), [typeof(bool)], [true]);
        Attribute(iface.SetCustomAttribute, typeof(GuidAttribute), [typeof(string)], [new Guid(Convert.FromHexString(fingerprint[..32])).ToString()]);
        Attribute(iface.SetCustomAttribute, typeof(InterfaceTypeAttribute), [typeof(ComInterfaceType)], [ComInterfaceType.InterfaceIsDual]);
        foreach (var variable in schema.Variables)
        {
            var property = iface.DefineProperty(variable.Name, PropertyAttributes.None, variable.ClrType, null);
            var get = iface.DefineMethod("get_" + variable.Name, MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.SpecialName, variable.ClrType, Type.EmptyTypes);
            DispId(get.SetCustomAttribute, variable.DispId);
            property.SetGetMethod(get);
        }
        foreach (var action in schema.Actions)
        {
            var args = action.Arguments.Where(a => !a.IsReturnValue).ToArray();
            var method = iface.DefineMethod(action.Name, MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig, action.Arguments.SingleOrDefault(a => a.IsReturnValue)?.Variable.ClrType ?? typeof(void),
                args.Select(a => a.IsInput ? a.Variable.ClrType : a.Variable.ClrType.MakeByRefType()).ToArray());
            DispId(method.SetCustomAttribute, action.DispId);
            for (var i = 0; i < args.Length; i++) method.DefineParameter(i + 1,
                args[i].IsInput ? ParameterAttributes.In : ParameterAttributes.In | ParameterAttributes.Out, args[i].Name);
        }
        var contract = iface.CreateType()!;
        var impl = module.DefineType("Service_" + fingerprint[..16], TypeAttributes.Public | TypeAttributes.Sealed,
            typeof(HostedServiceState), [contract, typeof(INativeUpnpEventSource)]);
        Attribute(impl.SetCustomAttribute, typeof(ComVisibleAttribute), [typeof(bool)], [true]);
        Attribute(impl.SetCustomAttribute, typeof(ClassInterfaceAttribute), [typeof(ClassInterfaceType)], [ClassInterfaceType.None]);
        Attribute(impl.SetCustomAttribute, typeof(ComDefaultInterfaceAttribute), [typeof(Type)], [contract]);
        impl.DefineDefaultConstructor(MethodAttributes.Public);
        foreach (var variable in schema.Variables)
        {
            var parent = contract.GetMethod("get_" + variable.Name)!;
            var get = Implement(impl, parent);
            var il = get.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, variable.Name);
            il.Emit(OpCodes.Call, typeof(HostedServiceState).GetMethod(nameof(HostedServiceState.Read))!);
            Cast(il, variable.ClrType); il.Emit(OpCodes.Ret);
        }
        foreach (var action in schema.Actions)
        {
            var method = Implement(impl, contract.GetMethod(action.Name)!);
            var args = action.Arguments.Where(a => !a.IsReturnValue).ToArray();
            var inputs = action.Arguments.Where(a => a.IsInput).ToArray();
            var outputs = action.Arguments.Where(a => !a.IsInput).ToArray();
            var il = method.GetILGenerator();
            var result = il.DeclareLocal(typeof(object[]));
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldstr, action.Name);
            il.Emit(OpCodes.Ldc_I4, inputs.Length); il.Emit(OpCodes.Newarr, typeof(object));
            for (var i = 0; i < inputs.Length; i++)
            {
                il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldarg, Array.IndexOf(args, inputs[i]) + 1);
                if (inputs[i].Variable.ClrType.IsValueType) il.Emit(OpCodes.Box, inputs[i].Variable.ClrType);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Call, typeof(HostedServiceState).GetMethod(nameof(HostedServiceState.Execute))!); il.Emit(OpCodes.Stloc, result);
            for (var i = 0; i < outputs.Length; i++)
            {
                if (outputs[i].IsReturnValue) continue;
                il.Emit(OpCodes.Ldarg, Array.IndexOf(args, outputs[i]) + 1);
                il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref);
                Cast(il, outputs[i].Variable.ClrType); il.Emit(OpCodes.Stobj, outputs[i].Variable.ClrType);
            }
            var returnIndex = Array.FindIndex(outputs, a => a.IsReturnValue);
            if (returnIndex >= 0)
            { il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ldc_I4, returnIndex); il.Emit(OpCodes.Ldelem_Ref); Cast(il, outputs[returnIndex].Variable.ClrType); }
            il.Emit(OpCodes.Ret);
        }
        return impl.CreateType()!;
    }
    private static MethodBuilder Implement(TypeBuilder type, MethodInfo method)
    {
        var builder = type.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
            MethodAttributes.HideBySig | (method.IsSpecialName ? MethodAttributes.SpecialName : 0), method.ReturnType,
            method.GetParameters().Select(p => p.ParameterType).ToArray());
        foreach (var parameter in method.GetParameters()) builder.DefineParameter(parameter.Position + 1, parameter.Attributes, parameter.Name);
        type.DefineMethodOverride(builder, method);
        return builder;
    }
    private static void Cast(ILGenerator il, Type type) => il.Emit(type.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, type);
    private static void DispId(Action<CustomAttributeBuilder> apply, int id) => Attribute(apply, typeof(DispIdAttribute), [typeof(int)], [id]);
    private static void Attribute(Action<CustomAttributeBuilder> apply, Type attribute, Type[] parameters, object[] values) =>
        apply(new CustomAttributeBuilder(attribute.GetConstructor(parameters)!, values));
}
