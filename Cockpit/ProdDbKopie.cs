using System.Diagnostics;
using System.Text;

namespace WorkManager;

/// <summary>Eén applicatie waarvoor de productiedatabank naar localhost gekopieerd kan worden.</summary>
public sealed class ProdDbDoel
{
    public required string Naam { get; init; }
    /// <summary>Lokale mysql-container in Docker/WSL waar de kopie terechtkomt.</summary>
    public required string Container { get; init; }
    /// <summary>Loginvlaggen voor de lokale mysql (wachtwoord staat in de docker-compose).</summary>
    public required string LokaalLogin { get; init; }
    /// <summary>Onderdelen die standaard uitgevinkt staan (regex, bv. oude _copy-tabellen).</summary>
    public string SkipPatroon { get; init; } = "(?!)";
    /// <summary>Korte uitleg voor het venster: waar komt de bron vandaan.</summary>
    public required string BronOmschrijving { get; init; }

    // -- Bron A: productie rechtstreeks bereikbaar; dump draait in de lokale container. --
    /// <summary>Linux-pad naar secrets.php met db_host/db_username/db_password/db_database.</summary>
    public string SecretsPad { get; init; } = "";
    /// <summary>Lokale databank waar de tabellen terechtkomen.</summary>
    public string DoelDb { get; init; } = "";

    // -- Bron B: databank alleen bereikbaar vanaf de hostingserver; dump loopt via SSH. --
    /// <summary>user@host van de hostingserver.</summary>
    public string SshDoel { get; init; } = "";
    /// <summary>SSH-sleutel in WSL (de deploytool-sleutel staat er al op).</summary>
    public string SshSleutel { get; init; } = "";
    /// <summary>Laravel-.env op de server met de databank-instellingen.</summary>
    public string RemoteEnvPad { get; init; } = "";
    /// <summary>Prefix van de .env-variabelen: "DB" (DB_HOST, …) of bv. "DB_MAIN" bij Aqurat.</summary>
    public string RemoteEnvPrefix { get; init; } = "DB";
    /// <summary>Lokale databank voor de hoofddatabank (DB_MAIN_DATABASE lokaal).</summary>
    public string HoofdDoelDb { get; init; } = "";
    /// <summary>
    /// Multi-tenant: ook de databanken uit de administrations-tabel meenemen en die tabel na
    /// de kopie omleiden naar de lokale tenant-databanken (host/gebruiker hieronder).
    /// </summary>
    public bool AdministratiesOmleiden { get; init; }
    public string AdminLokaalHost { get; init; } = "";
    public string AdminLokaalGebruiker { get; init; } = "";

    /// <summary>DataGrip-projectmap (onder ~\DataGripProjects) voor de knop na de kopie.</summary>
    public string DataGripProject { get; init; } = "";
    /// <summary>
    /// Datumfilter voor "alleen recente data": tabellen (regex) met hun datumkolom. Alleen
    /// zinvol bij grote historietabellen; tabellen zonder match worden altijd volledig
    /// gekopieerd.
    /// </summary>
    public (string TabelPatroon, string Kolom)[] DatumFilters { get; init; } =
        Array.Empty<(string, string)>();

    public bool ViaSsh => SshDoel.Length > 0;
}

/// <summary>
/// Kopieert een productiedatabank naar de lokale dev-mysql (Percona in Docker/WSL), zodat
/// aanpassingen met echte productiegegevens getest kunnen worden. Credentials komen bij elke
/// run rechtstreeks uit de configuratie van het project zelf (secrets.php in WSL, of de .env
/// op de hostingserver) — WorkManager bewaart geen wachtwoorden en het wachtwoord verlaat de
/// WSL/SSH-kant nooit.
///
/// Twee bronmodi:
///  • Rechtstreeks (RadiologyPartners): de Azure-databank is van hier bereikbaar; mysqldump
///    draait binnen de lokale mysql-container, tabel per tabel voor de voortgang. Productie
///    is daar case-insensitief terwijl lokaal case-sensitief draait: import gaat eerst naar
///    een tijdelijke databank en wisselt daarna om naar de bestaande lokale tabelnaam.
///  • Via SSH (Aqurat): de webhosting-databank laat alleen verbindingen uit de hosting toe;
///    mysqldump draait op de server (deploytool-sleutel), gecomprimeerd over SSH naar de
///    lokale container. Per databank (hoofd + één per administratie); na de kopie wordt de
///    administrations-tabel omgeleid naar de lokale tenant-databanken.
/// </summary>
public static class ProdDbKopie
{
    public static readonly ProdDbDoel RadiologyPartners = new()
    {
        Naam = "RadiologyPartners",
        Container = "devenv-mysql-1",
        LokaalLogin = "-h127.0.0.1 -uroot -pInitPWD1", // devenv/docker-compose.yml
        SkipPatroon = @"_copy\d*$",
        BronOmschrijving = "Azure-productiedatabank (credentials uit secrets.php van bloom-datawarehouse)",
        SecretsPad = "/home/maarten/projecten/bloom-datawarehouse/secrets.php",
        DoelDb = "bloom",
        DataGripProject = "RadiologypartnersEurope",
        DatumFilters = new[]
        {
            (@"^bloomstatistics_", "ServiceDate"),
            (@"^reporting(revenuesvisits)?$", "Date"),
        },
    };

    public static readonly ProdDbDoel Aqurat = new()
    {
        Naam = "Aqurat",
        Container = "aqurat-db1-1",
        LokaalLogin = "-h127.0.0.1 -uroot -pdb1pwd", // aqurat/infra/docker-compose.yml
        BronOmschrijving = "acc.aqurat.be via SSH (webhosting-databank is alleen daar bereikbaar); " +
                           "administrations wordt na de kopie omgeleid naar de lokale tenant-databanken",
        SshDoel = "aquratbe@ssh.aqurat.be",
        SshSleutel = "~/.ssh/deploytool",
        RemoteEnvPad = "subsites/acc.aqurat.be/.env",
        RemoteEnvPrefix = "DB_MAIN",
        HoofdDoelDb = "aqurat_main",
        AdministratiesOmleiden = true,
        AdminLokaalHost = "db1",
        AdminLokaalGebruiker = "root",
        DataGripProject = "Aqurat",
    };

    public static readonly ProdDbDoel Movaware = new()
    {
        Naam = "Movaware",
        Container = "devenv-mysql-1",
        LokaalLogin = "-h127.0.0.1 -uroot -pInitPWD1", // devenv/docker-compose.yml
        BronOmschrijving = "movaware.vriesveemlogistics.nl via SSH " +
                           "(webhosting-databank is alleen daar bereikbaar)",
        SshDoel = "movawarevriesveemlogisticsnl@ssh021.webhosting.be",
        SshSleutel = "~/.ssh/deploytool",
        RemoteEnvPad = ".env",
        HoofdDoelDb = "movaware",
        DataGripProject = "Movaware",
    };

    public static readonly ProdDbDoel CellawareNemijtek = new()
    {
        Naam = "Cellaware — Nemijtek",
        Container = "devenv-mysql-1",
        LokaalLogin = "-h127.0.0.1 -uroot -pInitPWD1", // devenv/docker-compose.yml
        BronOmschrijving = "cellaware.nemijtek.nl via SSH " +
                           "(webhosting-databank is alleen daar bereikbaar)",
        SshDoel = "cellawarenemijteknl@ssh.cellaware.nemijtek.nl",
        SshSleutel = "~/.ssh/deploytool",
        RemoteEnvPad = ".env",
        HoofdDoelDb = "cellaware_nemijtek",
        DataGripProject = "Cellaware",
    };

    public static readonly ProdDbDoel CellawareVriesveem = new()
    {
        Naam = "Cellaware — Vriesveem",
        Container = "devenv-mysql-1",
        LokaalLogin = "-h127.0.0.1 -uroot -pInitPWD1", // devenv/docker-compose.yml
        BronOmschrijving = "cellaware.vriesveem.nl via SSH " +
                           "(webhosting-databank is alleen daar bereikbaar)",
        SshDoel = "cellawarevriesveemnl@ssh.cellaware.vriesveem.nl",
        SshSleutel = "~/.ssh/deploytool",
        RemoteEnvPad = ".env",
        HoofdDoelDb = "vriesveem_new",
        DataGripProject = "Cellaware",
    };

    /// <summary>Alle applicaties met een kopieerbare productiedatabank.</summary>
    public static readonly ProdDbDoel[] Doelen =
        { RadiologyPartners, Aqurat, Movaware, CellawareNemijtek, CellawareVriesveem };

    private const string Distro = "Ubuntu";

    /// <summary>
    /// Eén kopieerbaar onderdeel: bij een rechtstreekse bron een tabel, bij een SSH-bron een
    /// hele databank (met de bijhorende bron-login en lokale doeldatabank).
    /// </summary>
    public sealed record KopieItem(string Naam, int Mb,
        string BronHost = "", string BronGebruiker = "", string BronDb = "",
        string DoelDb = "", string AdminId = "");

    // ---------------------------------------------------------------- items opsommen

    /// <summary>De kopieerbare onderdelen (tabellen of databanken) met hun grootte in MB.</summary>
    public static Task<List<KopieItem>> ItemsAsync(ProdDbDoel doel, CancellationToken ct) =>
        doel.ViaSsh ? SshItemsAsync(doel, ct) : DirecteItemsAsync(doel, ct);

    private static async Task<List<KopieItem>> DirecteItemsAsync(ProdDbDoel doel, CancellationToken ct)
    {
        var script = SecretsProloog(doel) + $$"""

            docker exec -e MYSQL_PWD {{doel.Container}} mysql -h "$H" -u "$U" --ssl-mode=REQUIRED \
              --connect-timeout=15 -N -B -e "select table_name, round(coalesce((data_length+index_length)/1024/1024,0)) \
              from information_schema.tables where table_schema='$D' and table_type='BASE TABLE' order by table_name"
            """;
        var uit = await BashAsync(script, TimeSpan.FromSeconds(90), ct);
        var items = new List<KopieItem>();
        foreach (var regel in Regels(uit))
        {
            var delen = regel.Split('\t');
            if (delen.Length == 2 && int.TryParse(delen[1], out var mb))
            {
                items.Add(new KopieItem(delen[0], mb));
            }
        }
        return items;
    }

    private static async Task<List<KopieItem>> SshItemsAsync(ProdDbDoel doel, CancellationToken ct)
    {
        // Op de server: hoofddatabank + (bij multi-tenant) administraties opsommen, elk met
        // hun grootte. Uitvoerformaat: MAIN|db|mb en ADM|id|naam|host|user|db|mb.
        var administraties = doel.AdministratiesOmleiden ? $$"""

            mysql -h "$H" -u "$U" "$D" -N -B -e "select id, name, host, username, \`database\` from administrations" 2>/dev/null |
            while IFS=$'\t' read -r id naam ahost auser adb; do
              AMB=$(mysql -h "$ahost" -u "$auser" -N -B -e "select round(coalesce(sum(data_length+index_length)/1024/1024,0)) from information_schema.tables where table_schema='$adb'" 2>/dev/null || echo 0)
              echo "ADM|$id|$naam|$ahost|$auser|$adb|$AMB"
            done
            """ : "";
        var remote = RemoteEnvProloog(doel) + $$"""

            MB=$(mysql -h "$H" -u "$U" -N -B -e "select round(coalesce(sum(data_length+index_length)/1024/1024,0)) from information_schema.tables where table_schema='$D'" 2>/dev/null)
            echo "MAIN|$D|$MB"
            """ + administraties;
        var uit = await SshAsync(doel, remote, TimeSpan.FromSeconds(120), ct);

        // Lokale mapping administratie-id → lokale tenant-databank, vóór de hoofddatabank
        // overschreven wordt (daarna staan er productiehosts in de lokale tabel).
        var lokaleAdmins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doel.AdministratiesOmleiden)
        {
            try
            {
                var mapping = await LokaalSqlAsync(doel,
                    $"SELECT id, `database` FROM `{doel.HoofdDoelDb}`.administrations;",
                    TimeSpan.FromMinutes(1), ct);
                foreach (var regel in Regels(mapping))
                {
                    var delen = regel.Split('\t');
                    if (delen.Length == 2)
                    {
                        lokaleAdmins[delen[0]] = delen[1];
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Lokale hoofddatabank of tabel bestaat (nog) niet: elke administratie
                // krijgt dan gewoon een nieuwe lokale databank.
            }
        }

        var items = new List<KopieItem>();
        var nieuwNr = 0;
        foreach (var regel in Regels(uit))
        {
            var d = regel.Split('|');
            if (d is ["MAIN", _, _] && int.TryParse(d[2], out var mainMb))
            {
                items.Add(new KopieItem($"hoofddatabank ({d[1]})", mainMb,
                    BronDb: d[1], DoelDb: doel.HoofdDoelDb));
            }
            else if (d is ["ADM", ..] && d.Length == 7 && int.TryParse(d[6], out var admMb))
            {
                // Bestaat de administratie lokaal al (zelfde id), dan komt de kopie in die
                // lokale databank; anders krijgt ze een eigen aqurat_admin_prod-databank.
                var lokaal = lokaleAdmins.GetValueOrDefault(d[1])
                    ?? $"{doel.HoofdDoelDb.Replace("_main", "")}_admin_prod{++nieuwNr}";
                items.Add(new KopieItem($"administratie {d[2]} ({d[5]})", admMb,
                    BronHost: d[3], BronGebruiker: d[4], BronDb: d[5],
                    DoelDb: lokaal, AdminId: d[1]));
            }
        }
        return items;
    }

    // ---------------------------------------------------------------- kopiëren

    /// <summary>
    /// Kopieert de gekozen onderdelen naar de lokale databank. Rapporteert per stap een
    /// statustekst en een voortgangsfractie (gewogen op grootte). Met
    /// <paramref name="filterMaanden"/> krijgen tabellen die in de DatumFilters van het doel
    /// passen alleen de laatste zoveel maanden mee (null = alles).
    /// </summary>
    public static Task KopieerAsync(ProdDbDoel doel, IReadOnlyList<KopieItem> items,
        int? filterMaanden, IProgress<(string Status, double Fractie)> voortgang,
        CancellationToken ct) =>
        doel.ViaSsh
            ? SshKopieerAsync(doel, items, voortgang, ct)
            : DirectKopieerAsync(doel, items, filterMaanden, voortgang, ct);

    /// <summary>De WHERE-clausule van het datumfilter voor deze tabel; leeg = geen filter.</summary>
    private static string DatumWhere(ProdDbDoel doel, string tabel, int? filterMaanden)
    {
        if (filterMaanden is not { } maanden)
        {
            return "";
        }
        var kolom = doel.DatumFilters.FirstOrDefault(f =>
            System.Text.RegularExpressions.Regex.IsMatch(
                tabel, f.TabelPatroon, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Kolom;
        return string.IsNullOrEmpty(kolom)
            ? ""
            : $"--where='{kolom} >= DATE_SUB(CURDATE(), INTERVAL {maanden} MONTH)' ";
    }

    private static async Task DirectKopieerAsync(ProdDbDoel doel, IReadOnlyList<KopieItem> items,
        int? filterMaanden, IProgress<(string Status, double Fractie)> voortgang, CancellationToken ct)
    {
        var tmp = doel.DoelDb + "_prodkopie";

        voortgang.Report(("Tijdelijke databank aanmaken…", 0));
        await LokaalSqlAsync(doel,
            $"DROP DATABASE IF EXISTS `{tmp}`; CREATE DATABASE `{tmp}` CHARACTER SET utf8mb4;",
            TimeSpan.FromMinutes(5), ct);

        // De bestaande lokale tabelnamen bepalen straks de juiste hoofdletters bij het
        // omwisselen (productie levert kleine letters, de code verwacht CamelCase).
        var lokaleNamen = Regels(await LokaalSqlAsync(doel, $"SHOW TABLES FROM `{doel.DoelDb}`;",
            TimeSpan.FromMinutes(1), ct));

        var totaalMb = items.Sum(t => Math.Max(1, t.Mb));
        var klaarMb = 0L;
        for (var i = 0; i < items.Count; i++)
        {
            var tabel = items[i];
            var waar = DatumWhere(doel, tabel.Naam, filterMaanden);
            voortgang.Report((
                $"Tabel {i + 1}/{items.Count}: {tabel.Naam} ({tabel.Mb} MB" +
                $"{(waar.Length > 0 ? $", laatste {filterMaanden} mnd" : "")}) overhalen…",
                klaarMb / (double)totaalMb * 0.97));
            var script = SecretsProloog(doel) + $$"""

                docker exec -e MYSQL_PWD {{doel.Container}} bash -c "set -o pipefail; \
                  mysqldump -h '$H' -u '$U' --ssl-mode=REQUIRED --single-transaction --quick \
                    --no-tablespaces --set-gtid-purged=OFF --default-character-set=utf8mb4 \
                    {{waar}}'$D' '{{tabel.Naam}}' \
                  | mysql {{doel.LokaalLogin}} --default-character-set=utf8mb4 \
                    --init-command='SET FOREIGN_KEY_CHECKS=0' '{{tmp}}'"
                """;
            // Ruime tijdslimiet die meegroeit met de tabel; de grootste is ~1,8 GB.
            await BashAsync(script, TimeSpan.FromMinutes(Math.Max(20, tabel.Mb / 5)), ct);
            klaarMb += Math.Max(1, tabel.Mb);
        }

        voortgang.Report(("Tabellen omwisselen naar de lokale databank…", 0.98));
        var sql = new StringBuilder("SET FOREIGN_KEY_CHECKS=0;\n");
        foreach (var tabel in items)
        {
            var doelNaam = lokaleNamen.FirstOrDefault(
                l => l.Equals(tabel.Naam, StringComparison.OrdinalIgnoreCase)) ?? tabel.Naam;
            sql.AppendLine($"DROP TABLE IF EXISTS `{doel.DoelDb}`.`{doelNaam}`;");
            sql.AppendLine($"RENAME TABLE `{tmp}`.`{tabel.Naam}` TO `{doel.DoelDb}`.`{doelNaam}`;");
        }
        sql.AppendLine($"DROP DATABASE `{tmp}`;");
        await LokaalSqlAsync(doel, sql.ToString(), TimeSpan.FromMinutes(10), ct);
        voortgang.Report(("Klaar", 1));
    }

    private static async Task SshKopieerAsync(ProdDbDoel doel, IReadOnlyList<KopieItem> items,
        IProgress<(string Status, double Fractie)> voortgang, CancellationToken ct)
    {
        var totaalMb = items.Sum(t => Math.Max(1, t.Mb));
        var klaarMb = 0L;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var tmp = item.DoelDb + "_prodkopie";
            voortgang.Report((
                $"{i + 1}/{items.Count}: {item.Naam} ({item.Mb} MB) → {item.DoelDb}…",
                klaarMb / (double)totaalMb * 0.95));

            await LokaalSqlAsync(doel,
                $"DROP DATABASE IF EXISTS `{tmp}`; CREATE DATABASE `{tmp}` CHARACTER SET utf8mb4;",
                TimeSpan.FromMinutes(5), ct);

            // Dump op de server (daar staat het wachtwoord, in de .env), gecomprimeerd over
            // SSH en meteen de lokale container in. Een lege BronHost = de hoofddatabank.
            var bron = item.BronHost.Length > 0
                ? $"mysqldump -h '{item.BronHost}' -u '{item.BronGebruiker}'"
                : "mysqldump -h \"$H\" -u \"$U\"";
            var remote = RemoteEnvProloog(doel) + $$"""

                {{bron}} --single-transaction --quick --no-tablespaces \
                  --default-character-set=utf8mb4 '{{item.BronDb}}' | gzip -c
                """;
            var script = MetRemoteScript(doel, remote) + $$"""

                ssh -i {{doel.SshSleutel}} -o BatchMode=yes -o ConnectTimeout=15 \
                  {{doel.SshDoel}} bash -s < "$REMOTE" \
                | gunzip \
                | docker exec -i {{doel.Container}} mysql {{doel.LokaalLogin}} \
                    --default-character-set=utf8mb4 --init-command='SET FOREIGN_KEY_CHECKS=0' '{{tmp}}'
                """;
            await BashAsync(script, TimeSpan.FromMinutes(Math.Max(20, item.Mb / 5)), ct);

            // Omwisselen: doeldatabank vers aanmaken en de tabellen erin schuiven.
            var tabellen = Regels(await LokaalSqlAsync(doel, $"SHOW TABLES FROM `{tmp}`;",
                TimeSpan.FromMinutes(1), ct));
            var sql = new StringBuilder("SET FOREIGN_KEY_CHECKS=0;\n");
            sql.AppendLine($"DROP DATABASE IF EXISTS `{item.DoelDb}`;");
            sql.AppendLine($"CREATE DATABASE `{item.DoelDb}` CHARACTER SET utf8mb4;");
            foreach (var tabel in tabellen)
            {
                sql.AppendLine($"RENAME TABLE `{tmp}`.`{tabel}` TO `{item.DoelDb}`.`{tabel}`;");
            }
            sql.AppendLine($"DROP DATABASE `{tmp}`;");
            await LokaalSqlAsync(doel, sql.ToString(), TimeSpan.FromMinutes(10), ct);
            klaarMb += Math.Max(1, item.Mb);
        }

        // De gekopieerde hoofddatabank wijst nog naar de productiehosts: administrations
        // omleiden naar de lokale tenant-databanken, anders start de app lokaal niets op.
        var admins = items.Where(t => t.AdminId.Length > 0).ToList();
        if (doel.AdministratiesOmleiden && admins.Count > 0)
        {
            voortgang.Report(("administrations omleiden naar de lokale databanken…", 0.98));
            var sql = new StringBuilder();
            foreach (var admin in admins)
            {
                sql.AppendLine(
                    $"UPDATE `{doel.HoofdDoelDb}`.administrations " +
                    $"SET host='{doel.AdminLokaalHost}', username='{doel.AdminLokaalGebruiker}', " +
                    $"`database`='{admin.DoelDb}' WHERE id='{admin.AdminId}';");
            }
            await LokaalSqlAsync(doel, sql.ToString(), TimeSpan.FromMinutes(1), ct);
        }
        voortgang.Report(("Klaar", 1));
    }

    // ---------------------------------------------------------------- bouwstenen

    /// <summary>Leest db_host/db_username/db_database/db_password uit secrets.php (in bash).</summary>
    private static string SecretsProloog(ProdDbDoel doel) => $$"""
        set -euo pipefail
        SECRETS='{{doel.SecretsPad}}'
        sec() { grep -oP "\"$1\"\s*=>\s*\"\K[^\"]+" "$SECRETS" | head -1; }
        H=$(sec db_host); U=$(sec db_username); D=$(sec db_database)
        export MYSQL_PWD=$(sec db_password)
        """;

    /// <summary>Leest host/gebruiker/databank/wachtwoord uit de .env op de server (in bash).</summary>
    private static string RemoteEnvProloog(ProdDbDoel doel) => $$"""
        set -euo pipefail
        E='{{doel.RemoteEnvPad}}'
        sec() { grep "^$1=" "$E" | head -1 | cut -d= -f2- | tr -d '\r'; }
        H=$(sec {{doel.RemoteEnvPrefix}}_HOST); U=$(sec {{doel.RemoteEnvPrefix}}_USERNAME); D=$(sec {{doel.RemoteEnvPrefix}}_DATABASE)
        export MYSQL_PWD=$(sec {{doel.RemoteEnvPrefix}}_PASSWORD)
        """;

    /// <summary>
    /// Zet een remote script klaar in een WSL-tempbestand ($REMOTE) voor `ssh bash -s`.
    /// Via een heredoc, zodat er geen quoting-laag bijkomt; via een bestand, omdat ssh de
    /// rest van onze eigen stdin zou opeten als we het script rechtstreeks zouden doorpompen.
    /// </summary>
    private static string MetRemoteScript(ProdDbDoel doel, string remote) => $$"""
        set -euo pipefail
        REMOTE=$(mktemp /tmp/workmanager-proddb-XXXXXX.sh)
        trap 'rm -f "$REMOTE"' EXIT
        cat > "$REMOTE" <<'EOREMOTE'
        {{remote}}
        EOREMOTE
        """;

    /// <summary>Draait een remote script op de hostingserver en geeft stdout terug.</summary>
    private static Task<string> SshAsync(
        ProdDbDoel doel, string remote, TimeSpan maxDuur, CancellationToken ct) =>
        BashAsync(MetRemoteScript(doel, remote) + $$"""

            ssh -i {{doel.SshSleutel}} -o BatchMode=yes -o ConnectTimeout=15 \
              {{doel.SshDoel}} bash -s < "$REMOTE" 2>/dev/null
            """, maxDuur, ct);

    /// <summary>Voert SQL uit op de lokale dev-mysql (via heredoc, dus backtick-veilig).</summary>
    private static Task<string> LokaalSqlAsync(
        ProdDbDoel doel, string sql, TimeSpan maxDuur, CancellationToken ct) =>
        BashAsync($"""
            set -euo pipefail
            docker exec -i {doel.Container} mysql {doel.LokaalLogin} --default-character-set=utf8mb4 -N -B <<'EOSQL'
            {sql}
            EOSQL
            """, maxDuur, ct);

    private static string[] Regels(string uitvoer) =>
        uitvoer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Draait een bash-script in WSL (aangeleverd via stdin, dus zonder quoting-gedoe op de
    /// Windows-commandoregel) en geeft stdout terug. Elke mislukking wordt een exceptie met
    /// de laatste foutregel; mysql-wachtwoordwaarschuwingen worden genegeerd.
    /// </summary>
    private static async Task<string> BashAsync(string script, TimeSpan maxDuur, CancellationToken ct)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = $"-d {Distro} -- bash -s",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        }) ?? throw new InvalidOperationException("wsl.exe kon niet gestart worden");

        await proc.StandardInput.WriteAsync(script.ReplaceLineEndings("\n"));
        proc.StandardInput.Close();

        var uitTaak = proc.StandardOutput.ReadToEndAsync(ct);
        var foutTaak = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(maxDuur);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* al weg */ }
            throw new OperationCanceledException(ct.IsCancellationRequested
                ? "Kopie geannuleerd"
                : $"Tijdslimiet van {maxDuur.TotalMinutes:0} min overschreden");
        }

        var fout = FilterRuis(await foutTaak);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(fout.Length > 0
                ? fout : $"bash eindigde met code {proc.ExitCode}");
        }
        return await uitTaak;
    }

    // ---------------------------------------------------------------- state

    /// <summary>Per doel onthouden: laatste kopie, uitgevinkte onderdelen en filterstand.</summary>
    public sealed class DoelState
    {
        public DateTimeOffset? LaatsteKopie { get; set; }
        public List<string> Uitgevinkt { get; set; } = new();
        public bool DatumFilterAan { get; set; } = true;
    }

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkManager", "prod-db-kopie.json");

    public static DoelState LaadState(ProdDbDoel doel)
    {
        try
        {
            if (File.Exists(StateFile) &&
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DoelState>>(
                    File.ReadAllText(StateFile)) is { } alles &&
                alles.TryGetValue(doel.Naam, out var state))
            {
                return state;
            }
        }
        catch
        {
            // Onleesbaar: verse state.
        }
        return new DoelState();
    }

    public static void BewaarState(ProdDbDoel doel, DoelState state)
    {
        try
        {
            Dictionary<string, DoelState>? alles = null;
            if (File.Exists(StateFile))
            {
                try
                {
                    alles = System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, DoelState>>(File.ReadAllText(StateFile));
                }
                catch
                {
                    // Kapot bestand: opnieuw beginnen.
                }
            }
            alles ??= new Dictionary<string, DoelState>();
            alles[doel.Naam] = state;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(
                alles, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: hooguit vergeet hij de selectie.
        }
    }

    private static string FilterRuis(string stderr)
    {
        var regels = Regels(stderr)
            .Where(r => !r.Contains("can be insecure", StringComparison.OrdinalIgnoreCase) &&
                        !r.Contains("Using a password", StringComparison.OrdinalIgnoreCase) &&
                        // De login-banner van de hostingserver is geen foutmelding.
                        !r.StartsWith('*') && !r.Contains("Unauthorized access") &&
                        !r.Contains("system/network is prohibited"))
            .ToList();
        // De laatste regels zijn doorgaans de echte foutmelding (mysqldump/mysql klagen daar).
        return string.Join(" · ", regels.TakeLast(3));
    }
}
