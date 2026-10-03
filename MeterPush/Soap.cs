using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

static class Soap
{
    static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    static readonly XNamespace Ser = "http://service.tg.spd";

    // Elements with a null/empty value are left out - every element is minOccurs=0 in the WSDL.
    public static string Envelope(string op, IEnumerable<(string Name, string? Value)> fields)
    {
        var body = new XElement(Ser + op);
        foreach (var (name, value) in fields)
            if (!string.IsNullOrEmpty(value)) body.Add(new XElement(Ser + name, value));

        return new XElement(SoapEnv + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", SoapEnv),
            new XAttribute(XNamespace.Xmlns + "ser", Ser),
            new XElement(SoapEnv + "Header"),
            new XElement(SoapEnv + "Body", body)).ToString();
    }

    public static (bool Ok, string Detail) Post(HttpClient http, string endpoint, string op, string xml)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            };
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"urn:{op}\"");
            using var resp = http.Send(req);
            var text = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();

            if (!resp.IsSuccessStatusCode || text.Contains("Fault>"))
                return (false, $"HTTP {(int)resp.StatusCode} {Truncate(text)}");

            var ret = XDocument.Parse(text).Descendants().FirstOrDefault(e => e.Name.LocalName == "return")?.Value;
            if (string.IsNullOrWhiteSpace(ret)) return (true, "(empty response)");
            ret = ret.Trim();
            // the service answers HTTP 200 even when its own insert failed, e.g. "Un-Successful:ORA-00984: ..."
            if (Regex.IsMatch(ret, @"^(un-?successful|fail|error)|ORA-\d+", RegexOptions.IgnoreCase))
                return (false, ret);
            return (true, ret);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "...";
}
