using System.Globalization;
using System.Net;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record HostsSettings(string[] Domains, string Transport, string Server, Uri? DohEndpoint, string Bootstrap, string Family, bool UseSystemProxy = false);

public static class HostsCapabilities
{
    public const string Update = "hosts.update";
    public const string TaskId = "526645ee-95d7-4bec-b5aa-04674b587217";
    public static TaskCapability Descriptor { get; } = new(Update, 1, TaskCapabilityKind.Action, "网络", "更新 Hosts",
        "通过指定 DNS 解析域名，更新系统 hosts 的 Axiom 管理区；保留其他内容。",
        [new("domains", "域名名单", TaskFieldKind.TextList, true, "每行一个域名，最多16个；不含协议、端口、路径或通配符。", null, 4096, [], EditAsList: true),
         new("transport", "DNS 方式", TaskFieldKind.Choice, true, "普通DNS使用UDP，截断时转TCP；DoH使用HTTPS。", "doh", 8, [new("dns", "普通 DNS"), new("doh", "DNS over HTTPS（DoH）")]),
         new("useSystemProxy", "使用系统代理", TaskFieldKind.Boolean, false, "仅用于DoH。开启后按系统代理和绕过规则连接，服务器地址由系统或代理解析；Bootstrap仅用于关闭时的直连。未配置代理或命中绕过规则时可能直连，普通DNS仍直连。", "false", 5, []),
         new("server", "普通 DNS 服务器", TaskFieldKind.Text, true, "单个DNS服务器IP，不填域名或DoH网址；可带端口，IPv6带端口使用[地址]:端口。", "1.1.1.1", 80, []),
         new("doh", "DoH 地址", TaskFieldKind.Text, true, "完整HTTPS地址，例如 https://cloudflare-dns.com/dns-query；使用标准DNS报文。", "https://cloudflare-dns.com/dns-query", 2048, []),
         new("bootstrap", "Bootstrap DNS", TaskFieldKind.Text, true, "单个普通DNS服务器IP（可带端口），用于解析DoH服务器域名；不填域名、DoH网址或多个地址。DoH服务器为IP时无需解析。", "1.1.1.1", 80, []),
         new("family", "写入地址", TaskFieldKind.Choice, true, "双栈会查询A与AAAA；至少须得到一个对应IP。", "ipv4", 8, [new("ipv4", "IPv4"), new("ipv6", "IPv6"), new("both", "IPv4 和 IPv6")])],
        "检测满足后整批解析，全部成功才更新；失败保留旧记录。同名手工记录冲突时拒绝写入。关闭窗口仍工作，暂停停止后续执行。",
        "暂停、删除或移除域名不会撤销已经写入的记录。仅更新本次名单的管理记录，其他管理记录保留；如需撤销请手动编辑系统hosts。已打开连接及应用自身DNS缓存可能继续使用旧IP。",
        SupportsTimeWindow: true);

    public static string Domain(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl)) throw new ArgumentException("域名无效。");
        string ascii;
        try { ascii = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { throw new ArgumentException("域名无效。"); }
        if (ascii.Length is < 1 or > 253 || IPAddress.TryParse(ascii, out _) || ascii.Split('.').Any(label =>
            label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("请填写有效域名，不含协议、端口、路径或通配符。");
        return ascii;
    }
    public static IPEndPoint Endpoint(string value)
    {
        var withPort = value.StartsWith('[') || value.Count(c => c == ':') == 1;
        if (!withPort && IPAddress.TryParse(value, out var address)) return new(address, 53);
        if (withPort && IPEndPoint.TryParse(value, out var endpoint) && endpoint.Port is >= 1 and <= 65535) return endpoint;
        throw new ArgumentException("DNS服务器须为IP地址，可附带1–65535端口。");
    }
    public static HostsSettings Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Update || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count is < 6 or > 7 ||
            invocation.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k)) ||
            Descriptor.Fields.Any(f => invocation.Parameters.TryGetValue(f.Id, out var v) ? string.IsNullOrWhiteSpace(v) || v.Length > f.MaximumLength : f.Required))
            throw new ArgumentException("Hosts配置无效，请填写域名、DNS和地址类型。");
        var p = invocation.Parameters;
        var useProxy = p.GetValueOrDefault("useSystemProxy");
        if (useProxy is not (null or "true" or "false")) throw new ArgumentException("请选择是否使用系统代理。");
        var domains = p["domains"].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Domain).Distinct().ToArray();
        if (domains.Length is < 1 or > 16) throw new ArgumentException("请选择1–16个不同域名。");
        if (p["transport"] is not ("dns" or "doh") || p["family"] is not ("ipv4" or "ipv6" or "both")) throw new ArgumentException("DNS方式或地址类型无效。");
        void ValidateServer(string value, string label)
        {
            try { _ = Endpoint(value); }
            catch (ArgumentException) { throw new ArgumentException($"请将“{label}”填写为单个DNS服务器IP地址，可带端口；不要填写域名、DoH网址或多个地址。"); }
        }
        ValidateServer(p["server"], "普通 DNS 服务器"); ValidateServer(p["bootstrap"], "Bootstrap DNS");
        if (!Uri.TryCreate(p["doh"], UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Length == 0)
            throw new ArgumentException("DoH须为有效HTTPS地址，不含用户信息或片段。");
        return new(domains, p["transport"], p["server"], uri, p["bootstrap"], p["family"], useProxy == "true");
    }
}
