using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WorkManager;

/// <summary>
/// Beoordeelt per mail of een antwoord van toepassing is en stelt in dat geval een
/// conceptantwoord op; kan een bestaand concept ook herwerken op basis van feedback.
/// Draait via de Claude Code CLI ('claude -p') op het bestaande Claude-abonnement —
/// er is dus geen API-key nodig, alleen een ingelogde CLI.
/// </summary>
public static class ClaudeDrafter
{
    public sealed record Resultaat(bool Antwoorden, bool Actie, bool Urgent, string Reden, string Concept);

    private const int MaxBodyTekens = 8000;

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    public static async Task<Resultaat> DraftAsync(
        MailBericht mail, string instructies, MailReplySettings settings, CancellationToken ct,
        string historiek = "")
    {
        // Klantdossier: blijvende achtergrondkennis over deze klant (wie is wie, welke
        // software, welke afspraken, welk jargon). Staat los van de recente historiek.
        var dossier = KlantDossier.Voor(mail.VanAdres);
        var intro = mail.IsChat
            ? """
              Je bent de berichtenassistent van Maarten. Hieronder staat het recente verloop van één
              chatgesprek (Google Chat of WhatsApp; regels van "Maarten (ikzelf)" zijn zijn eigen
              berichten, en het gesprek loopt chronologisch: het LAATSTE bericht staat onderaan).
              Beoordeel of een antwoord van Maarten van toepassing is en stel in dat geval een kort
              chatbericht op dat rechtstreeks reageert op dat laatste bericht — de eerdere berichten
              zijn alleen context, beantwoord die niet opnieuw. Zelfde taal als het gesprek, geen
              aanhef en geen afsluiting. Geen antwoord als het laatste bericht van Maarten zelf komt
              of als het gesprek duidelijk geen reactie meer verwacht.
              """
            : $"""
              Je bent de e-mailassistent van {settings.Email}. Hieronder staat één binnengekomen e-mail.
              Beoordeel of een persoonlijk antwoord van toepassing is en stel in dat geval een volledig
              conceptantwoord op (alleen de mailtekst, zonder onderwerpregel).

              Schrijf het antwoord in DEZELFDE taal als de e-mail (Nederlands, Frans of Engels): een
              Franse mail beantwoord je in het Frans, een Engelse in het Engels.

              Geen antwoord bij: nieuwsbrieven, reclame, automatische notificaties of bevestigingen,
              no-reply-afzenders, en mails die duidelijk geen reactie van de ontvanger verwachten.
              """;
        var prompt =
            $$"""
            {{intro}}

            Schrijf het concept strikt volgens deze stijl-skill van de gebruiker{{(mail.IsChat
                ? " (toon en taalkeuze; de mail-opmaak met aanhef/afsluiting geldt niet voor chat)"
                : "")}}:
            ---
            {{instructies}}
            ---

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"antwoorden": true of false, "actie": true als dit een actie of opvolging van Maarten vraagt (ook zonder direct antwoord) anders false, "urgent": true als een antwoord het best vandaag nog vertrekt (dringende vraag, deadline, wachtende klant) anders false, "reden": "korte reden in het Nederlands (max. één zin)", "concept": "volledige antwoordtekst; lege string als antwoorden false is"}

            {{(dossier.Length > 0
                ? $"""

                  KLANTDOSSIER — vaste achtergrondkennis over deze klant en de samenwerking.
                  Gebruik dit om de vraag juist te begrijpen, het jargon van de klant over te
                  nemen en niets te beloven wat tegen bestaande afspraken ingaat. Verzin niets
                  bij: staat een detail hier niet in en is het nodig voor het antwoord, stel
                  dan in het concept expliciet de vraag aan de klant.
                  ---
                  {dossier}
                  ---
                  """
                : "")}}

            {{(mail.IsChat ? "Chatgesprek" : "E-mail")}}:
            {{BeschrijfMail(mail)}}
            {{(historiek.Length > 0
                ? $"""

                  Ter context — recente correspondentie met deze persoon (oudste eerst; gebruik dit
                  voor toon, lopende afspraken en openstaande punten, maar beantwoord alléén de
                  e-mail hierboven):
                  ---
                  {historiek}
                  ---
                  """
                : "")}}
            """;

        var output = await RunClaudeAsync(prompt, ct);
        using var doc = ParseJson(output);
        var root = doc.RootElement;
        return new Resultaat(
            root.TryGetProperty("antwoorden", out var a) && a.ValueKind == JsonValueKind.True,
            root.TryGetProperty("actie", out var actie) && actie.ValueKind == JsonValueKind.True,
            root.TryGetProperty("urgent", out var urgent) && urgent.ValueKind == JsonValueKind.True,
            root.TryGetProperty("reden", out var r) ? r.GetString() ?? "" : "",
            root.TryGetProperty("concept", out var c) ? c.GetString() ?? "" : "");
    }

    /// <summary>Herwerkt een bestaand concept op basis van feedback, opnieuw volgens de stijl-skill.</summary>
    public static async Task<string> ReviseAsync(
        MailBericht mail, string huidigConcept, string feedback, string instructies,
        MailReplySettings settings, CancellationToken ct)
    {
        var dossier = KlantDossier.Voor(mail.VanAdres);
        var prompt =
            $$"""
            Je bent de e-mailassistent van {{settings.Email}}. Hieronder staan een binnengekomen e-mail,
            het huidige conceptantwoord en feedback van Maarten op dat concept.
            Herschrijf het conceptantwoord volgens de feedback. Blijf daarbij strikt de stijl-skill
            van de gebruiker volgen (toon, aanhef, afsluiting, taal):
            ---
            {{instructies}}
            ---

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"concept": "de volledige herschreven antwoordtekst, zonder onderwerpregel"}
            {{(dossier.Length > 0
                ? $"""

                  KLANTDOSSIER — vaste achtergrondkennis over deze klant (jargon, afspraken,
                  lopende dossiers). Verzin niets bij wat hier niet in staat.
                  ---
                  {dossier}
                  ---
                  """
                : "")}}

            E-mail:
            {{BeschrijfMail(mail)}}

            Huidig conceptantwoord:
            ---
            {{huidigConcept}}
            ---

            Feedback van Maarten:
            ---
            {{feedback}}
            ---
            """;

        var output = await RunClaudeAsync(prompt, ct);
        using var doc = ParseJson(output);
        return doc.RootElement.TryGetProperty("concept", out var c) ? c.GetString() ?? "" : "";
    }

    /// <summary>
    /// Kort bevestigingsconcept bij "taak maken van bericht": laat de afzender weten dat
    /// Maarten het gezien heeft, het op de taakdatum oppakt en hem op de hoogte houdt —
    /// zonder inhoudelijk te antwoorden. Zelfde taal als het bericht, volgens de stijl-skill.
    /// </summary>
    public static async Task<string> TaakBevestigingAsync(
        MailBericht mail, DateOnly? datum, string instructies, MailReplySettings settings,
        CancellationToken ct)
    {
        var wanneer = datum is { } d
            ? $"op {d.ToDateTime(TimeOnly.MinValue):dddd d MMMM yyyy} (formuleer die dag natuurlijk " +
              "in de taal van het bericht; valt hij binnen een week, dan volstaat de weekdag)"
            : "binnenkort";
        var prompt =
            $$"""
            Je bent de e-mailassistent van {{settings.Email}}. Maarten heeft van onderstaand
            bericht een taak gemaakt: hij pakt dit {{wanneer}} op. Schrijf een kort, vriendelijk
            antwoord aan de afzender dat bevestigt dat hij het gezien heeft, dat hij het dan
            oppakt en dat hij de afzender op de hoogte houdt. Ga NIET inhoudelijk op de vraag
            in en beloof niets anders. Schrijf in DEZELFDE taal als het bericht.

            Schrijf het concept strikt volgens deze stijl-skill van de gebruiker{{(mail.IsChat
                ? " (toon en taalkeuze; de mail-opmaak met aanhef/afsluiting geldt niet voor chat)"
                : "")}}:
            ---
            {{instructies}}
            ---

            Antwoord UITSLUITEND met één JSON-object, zonder verdere tekst of markdown eromheen:
            {"concept": "volledige antwoordtekst, zonder onderwerpregel"}

            {{(mail.IsChat ? "Chatgesprek" : "E-mail")}}:
            {{BeschrijfMail(mail)}}
            """;
        var output = await RunClaudeAsync(prompt, ct);
        using var doc = ParseJson(output);
        return doc.RootElement.TryGetProperty("concept", out var c) ? c.GetString() ?? "" : "";
    }

    private static string BeschrijfMail(MailBericht mail)
    {
        var body = mail.Tekst.Length > MaxBodyTekens
            ? mail.Tekst[..MaxBodyTekens] + "\n[… ingekort …]"
            : mail.Tekst;
        return mail.IsChat
            ? $"""
              Gesprek: {mail.Van}
              Laatste bericht: {mail.Datum:yyyy-MM-dd HH:mm}

              {body}
              """
            : $"""
              Van: {mail.Van} <{mail.VanAdres}>
              Onderwerp: {mail.Onderwerp}
              Datum: {mail.Datum:yyyy-MM-dd HH:mm}

              {body}
              """;
    }

    internal static async Task<string> RunClaudeAsync(string prompt, CancellationToken ct)
    {
        // Via cmd zodat ook een npm-installatie (claude.cmd) gevonden wordt; de prompt gaat
        // via stdin zodat er geen quoting-problemen zijn. Werkmap is de WorkManager-datamap
        // (geen projectmap, dus geen CLAUDE.md of projectcontext die meespeelt).
        Directory.CreateDirectory(DataDir);
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c claude -p",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = DataDir,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Kon de Claude CLI niet starten.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                var fout = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (fout.Contains("niet herkend", StringComparison.OrdinalIgnoreCase) ||
                    fout.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "De 'claude' CLI is niet gevonden op het PATH. Is Claude Code geïnstalleerd?");
                }
                throw new InvalidOperationException($"Claude CLI gaf exitcode {proc.ExitCode}: {Kort(fout)}");
            }
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Proces was al gestopt.
            }
            throw;
        }
    }

    internal static JsonDocument ParseJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException($"Geen JSON in het antwoord van Claude: {Kort(output)}");
        }
        return JsonDocument.Parse(output[start..(end + 1)]);
    }

    private static string Kort(string tekst)
    {
        tekst = tekst.Trim();
        return tekst.Length > 300 ? tekst[..300] + "…" : tekst;
    }
}
