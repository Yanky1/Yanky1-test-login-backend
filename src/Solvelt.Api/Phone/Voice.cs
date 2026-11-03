using System.Text;
using System.Xml;
using Microsoft.Extensions.Options;
using Solvelt.Api.Options;

namespace Solvelt.Api.Phone;

public interface IVoiceResponseBuilder
{
    VoiceResponse Create();
}

/// <summary>Builds TwiML/LaML. All text and attribute values go through XmlWriter, so they are always escaped.</summary>
public class VoiceResponse(string voice, string language)
{
    private readonly List<Action<XmlWriter>> _verbs = [];

    public VoiceResponse Say(string text) { _verbs.Add(w => { w.WriteStartElement("Say"); w.WriteAttributeString("voice", voice); w.WriteAttributeString("language", language); w.WriteString(text); w.WriteEndElement(); }); return this; }
    public VoiceResponse Pause(int seconds) { _verbs.Add(w => { w.WriteStartElement("Pause"); w.WriteAttributeString("length", seconds.ToString()); w.WriteEndElement(); }); return this; }
    public VoiceResponse Hangup() { _verbs.Add(w => { w.WriteStartElement("Hangup"); w.WriteEndElement(); }); return this; }
    public VoiceResponse Reject() { _verbs.Add(w => { w.WriteStartElement("Reject"); w.WriteAttributeString("reason", "rejected"); w.WriteEndElement(); }); return this; }
    public VoiceResponse Dial(string number) { _verbs.Add(w => { w.WriteStartElement("Dial"); w.WriteString(number); w.WriteEndElement(); }); return this; }

    /// <summary>
    /// Prompt + input. Secret steps (PIN, codes) use keypad only (input="dtmf"), so they are never transcribed; other steps use input="speech dtmf" so the keypad always works too.
    /// '#' submits. Without input the provider still posts to the action (actionOnEmptyResult), so silence is handled server-side.
    /// </summary>
    public VoiceResponse Gather(string action, IEnumerable<string> lines, bool speech, int? numDigits = null, int timeoutSeconds = 6)
    {
        _verbs.Add(w =>
        {
            w.WriteStartElement("Gather");
            w.WriteAttributeString("input", speech ? "speech dtmf" : "dtmf");
            w.WriteAttributeString("action", action);
            w.WriteAttributeString("method", "POST");
            w.WriteAttributeString("timeout", timeoutSeconds.ToString());
            w.WriteAttributeString("finishOnKey", "#");
            w.WriteAttributeString("actionOnEmptyResult", "true");
            if (speech) { w.WriteAttributeString("speechTimeout", "auto"); w.WriteAttributeString("language", language); }
            if (numDigits is { } n) w.WriteAttributeString("numDigits", n.ToString());
            foreach (var line in lines) { w.WriteStartElement("Say"); w.WriteAttributeString("voice", voice); w.WriteAttributeString("language", language); w.WriteString(line); w.WriteEndElement(); }
            w.WriteEndElement();
        });
        return this;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false), Indent = false }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Response");
            foreach (var v in _verbs) v(w);
            w.WriteEndElement();
        }
        return sb.ToString();
    }

    /// <summary>"123456" -> "1, 2, 3, 4, 5, 6": read digit by digit.</summary>
    public static string Digits(string digits) => string.Join(", ", digits.Where(char.IsAsciiDigit));

    /// <summary>SMS reply document (inline TwiML). Empty text gives an empty response (nothing is sent).</summary>
    public static string Message(string? text)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Response");
            if (!string.IsNullOrEmpty(text)) { w.WriteStartElement("Message"); w.WriteString(text); w.WriteEndElement(); }
            w.WriteEndElement();
        }
        return sb.ToString();
    }
}

public class VoiceResponseBuilder(IOptions<PhoneOptions> opt) : IVoiceResponseBuilder
{
    public VoiceResponse Create() => new(opt.Value.Voice, opt.Value.Language);
}
