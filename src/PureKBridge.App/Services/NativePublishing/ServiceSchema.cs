using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PureKBridge.App.Services.NativePublishing;

internal sealed record ServiceVariable(string Name, string DataType, bool Evented, string? DefaultValue, int DispId)
{
    public Type ClrType => DataType switch
    {
        "ui1" => typeof(byte), "ui2" => typeof(ushort), "ui4" => typeof(uint), "i1" => typeof(sbyte),
        "i2" => typeof(short), "i4" or "int" => typeof(int), "r4" => typeof(float),
        "r8" or "number" or "float" => typeof(double), "fixed.14.4" => typeof(decimal),
        "boolean" => typeof(bool), "bin.base64" or "bin.hex" => typeof(byte[]),
        "date" or "dateTime" or "dateTime.tz" or "time" or "time.tz" => typeof(string),
        "string" or "char" or "uri" or "uuid" => typeof(string),
        _ => throw new InvalidDataException("不支持的 UPnP 数据类型：" + DataType)
    };
    public object Parse(string value) => DataType switch
    {
        "boolean" => value.Trim() switch { "1" or "true" or "yes" => true, "0" or "false" or "no" => false,
            _ => throw new InvalidDataException("UPnP boolean 格式无效。") },
        "bin.base64" => Convert.FromBase64String(value), "bin.hex" => Convert.FromHexString(value),
        _ => ClrType == typeof(string) ? value : Convert.ChangeType(value, ClrType, CultureInfo.InvariantCulture)
    };
    public string Format(object? value) => value switch
    {
        bool boolean => boolean ? "1" : "0", byte[] bytes => DataType == "bin.hex" ? Convert.ToHexString(bytes) : Convert.ToBase64String(bytes),
        null => "", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
    public object Default => DefaultValue is not null ? Parse(DefaultValue) : ClrType == typeof(string) ? "" :
        ClrType == typeof(byte[]) ? Array.Empty<byte>() : Activator.CreateInstance(ClrType)!;
}
internal sealed record ServiceArgument(string Name, bool IsInput, bool IsReturnValue, ServiceVariable Variable);
internal sealed record ServiceAction(string Name, IReadOnlyList<ServiceArgument> Arguments, int DispId);
internal sealed record ServiceSchema(string Xml, IReadOnlyList<ServiceVariable> Variables, IReadOnlyList<ServiceAction> Actions)
{
    public static XDocument ReadXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        return XDocument.Load(reader);
    }
    public static ServiceSchema Parse(string xml)
    {
        var document = ReadXml(xml);
        var root = document.Root ?? throw new InvalidDataException("SCPD 为空。");
        if (root.Name != XName.Get("scpd", "urn:schemas-upnp-org:service-1-0")) throw new InvalidDataException("SCPD 命名空间无效。");
        static string Field(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
        static string Name(string value)
        {
            if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z_0-9]{0,95}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("SCPD 名称无效。");
            return value;
        }
        var variables = root.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceStateTable")?.Elements()
            .Where(e => e.Name.LocalName == "stateVariable").Select((e, i) => new ServiceVariable(Name(Field(e, "name")),
                Field(e, "dataType"), !string.Equals(e.Attribute("sendEvents")?.Value, "no", StringComparison.OrdinalIgnoreCase),
                e.Elements().FirstOrDefault(x => x.Name.LocalName == "defaultValue")?.Value, i + 1)).ToArray() ?? [];
        if (variables.Length is 0 or > 256 || variables.Select(v => v.Name).Distinct().Count() != variables.Length)
            throw new InvalidDataException("SCPD 状态变量为空、过多或重名。");
        foreach (var variable in variables) _ = variable.ClrType;
        var actions = root.Elements().FirstOrDefault(e => e.Name.LocalName == "actionList")?.Elements()
            .Where(e => e.Name.LocalName == "action").Select((e, i) => new ServiceAction(Name(Field(e, "name")),
                e.Elements().FirstOrDefault(x => x.Name.LocalName == "argumentList")?.Elements().Select(a =>
                {
                    var direction = Field(a, "direction");
                    if (direction is not ("in" or "out")) throw new InvalidDataException("SCPD 参数方向无效。");
                    var variable = variables.SingleOrDefault(v => v.Name == Field(a, "relatedStateVariable")) ??
                        throw new InvalidDataException("SCPD 参数缺少对应状态变量。");
                    return new ServiceArgument(Name(Field(a, "name")), direction == "in",
                        a.Elements().Any(x => x.Name.LocalName == "retval"), variable);
                }).ToArray() ?? [], 1000 + i)).ToArray() ?? [];
        if (actions.Length is 0 or > 128 || actions.Select(a => a.Name).Distinct().Count() != actions.Length ||
            actions.Any(a => a.Arguments.Count > 32 || a.Arguments.Select(x => x.Name).Distinct().Count() != a.Arguments.Count ||
                a.Arguments.Count(x => x.IsReturnValue) > 1 || a.Arguments.Any(x => x.IsReturnValue && x.IsInput)) ||
            actions.Any(a => variables.Any(v => v.Name == a.Name))) throw new InvalidDataException("SCPD 动作定义无效。");
        return new(xml, variables, actions);
    }
}
