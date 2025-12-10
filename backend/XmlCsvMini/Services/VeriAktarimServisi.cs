using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using XmlCsvMini.Models;            // 👉 VeriAktarimSonucu, TabloOzeti, SutunOzeti burada
using Npgsql;
using NpgsqlTypes;

namespace XmlCsvMini.Services
{
    /// <summary>
    /// ZIP içindeki CSV'leri okuyup DOĞRUDAN tabloya yükler;
    /// dosya adı -> tablo, header -> kolon; tipleri keşfeder ve CREATE TABLE + COPY yapar.
    /// </summary>
    public sealed class VeriAktarimServisi : IVeriAktarimServisi
    {
        private readonly ILogger<VeriAktarimServisi> _log;
        private readonly string _connStr;

        private readonly string _schema; // 👉 Direct mod hedef şema (appsettings: Import:DirectSchema veya "import")

        public VeriAktarimServisi(ILogger<VeriAktarimServisi> log, IConfiguration cfg)
        {
            _log = log;
            _connStr = cfg.GetConnectionString("PostgreDb")
                      ?? throw new InvalidOperationException("ConnectionStrings:PostgreDb bulunamadı.");

            _schema = cfg.GetValue<string>("Import:DirectSchema")?.Trim();
            if (string.IsNullOrWhiteSpace(_schema))
                _schema = "deneme_schema"; // 👉 şema belirtilmemişse "import" kullan
        }

        public async Task<VeriAktarimSonucu> ZiptenVeritabaninaAktarAsync(
    string zipYolu,
    string? loadType = null,
    DateTime? dataDate = null,
    CancellationToken ct = default)
        {
            if (!File.Exists(zipYolu))
                throw new FileNotFoundException("ZIP dosyası bulunamadı", zipYolu);

            var sonuc = new VeriAktarimSonucu();

            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);

            // 👉 Hedef şemayı yoksa oluştur (idempotent) - veya sıfırla
            // await ResetSchemaAsync(conn, _schema, ct);
            // 1) Şemayı silmek yok, sadece varsa kullan; yoksa oluştur
            await EnsureSchemaAsync(conn, _schema, ct);

            // 2) Bu import için bir batchId ve dataDate üret
            var batchId = Guid.NewGuid();

            var effectiveLoadType = string.IsNullOrWhiteSpace(loadType)
                ? "Direct"
                : loadType;

            var effectiveDataDate = (dataDate ?? DateTime.UtcNow).Date;

            await EnsureImportBatchesTableAsync(conn, ct);
            await KaydetImportBatchAsync(
                conn,
                batchId,
                Path.GetFileName(zipYolu),
                effectiveLoadType,   // ← Artık gerçekten Full/Daily/Direct ne geldiyse
                effectiveDataDate,   // ← Her zaman non-null DateTime
                ct);


            using var arsiv = ZipFile.OpenRead(zipYolu);

            // 👉 ZIP içindeki tüm .csv dosyalarını al
            var csvEntries = arsiv.Entries
                .Where(e => !string.IsNullOrEmpty(e.FullName) && e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (csvEntries.Count == 0)
            {
                _log.LogWarning("ZIP içinde CSV bulunamadı: {zip}", zipYolu);
                return sonuc;
            }

            // ============================================================
            // 1. ADIM: Header Haritasını ve Tablo Listesini Hazırla
            // ============================================================

            // Tüm tablo adları (Listeleme vs. için kenarda dursun)
            var tumTabloAdlari = csvEntries
                .Select(e => Path.GetFileNameWithoutExtension(e.FullName))
                .ToList();

            // 👉 YENİ: Her tablo için header'ları (kolon adlarını) toplayalım
            // Bu harita, dinamik filtreleme mantığı için kullanılacak.
            var headerHaritasi = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in csvEntries)
            {
                var rawTableName = Path.GetFileNameWithoutExtension(entry.FullName);

                using var srHeader = new StreamReader(
                    entry.Open(),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);

                var headerLine = await srHeader.ReadLineAsync();
                if (string.IsNullOrEmpty(headerLine))
                    continue;

                // Mevcut SplitCsvLine fonksiyonunu kullanıyoruz
                var csvCols = SplitCsvLine(headerLine).ToArray();
                headerHaritasi[rawTableName] = csvCols;
            }

            // ============================================================
            // 2. ADIM: Dosyaları İşle
            // ============================================================
            foreach (var entry in csvEntries)
            {
                ct.ThrowIfCancellationRequested();

                // 👉 Dosya adı -> tablo adı (temizlenir: TR karakterler/boşluk -> güvenli identifier)
                var rawTable = Path.GetFileNameWithoutExtension(entry.FullName);
                var table = SanitizeIdentifier(rawTable);

                // ============================================================
                // FİLTRE: Gereksiz tabloları (alan kırıklarını) atla
                // ============================================================
                // 👉 Artık tumTabloAdlari yerine headerHaritasi kullanıyoruz (Dinamik Mantık)
                if (TabloDbIcinGereksiz(rawTable, headerHaritasi))
                {
                    _log.LogInformation("Tablo atlanıyor (dinamik alan kırığı): {schema}.{table}", _schema, table);
                    continue; // Döngü başa döner, bu dosya işlenmez
                }

                _log.LogInformation("İşleniyor (direct): {schema}.{table}", _schema, table);

                // ---------- 1) İlk geçiş: tip keşfi + özet ----------
                // 👉 headere göre kolonları oku; her kolonda gelen değerlerle tip keşfi yap
                string[] kolonAdlari;
                SutunOzeti[] sutunOzeti;
                long satirSayisi = 0;

                using (var sr1 = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                {
                    var header = await sr1.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(header))
                    {
                        _log.LogWarning("{table} boş ya da başlık satırı yok.", table);
                        continue; // bu dosyayı atla
                    }

                    // 👉 Header kolonlarını temizle (identifier güvenliği)
                    kolonAdlari = SplitCsvLine(header).Select(SanitizeIdentifier).ToArray();
                    var kolonCount = kolonAdlari.Length;

                    // 👉 Her kolon için başlangıç tipi string kabul; ilerledikçe Infer/Merge ile genişlet
                    sutunOzeti = kolonAdlari
                        .Select(ad => new SutunOzeti { Ad = ad, TanimlananTur = "unknown", Nullable = false, BosDegerSayisi = 0 })
                        .ToArray();

                    string? line;
                    while ((line = await sr1.ReadLineAsync()) != null)
                    {
                        var values = SplitCsvLine(line).ToArray();

                        for (int i = 0; i < kolonCount; i++)
                        {
                            string? deger = i < values.Length ? values[i] : null;

                            if (string.IsNullOrEmpty(deger))
                            {
                                // 👉 boş değer: kolonu nullable yap, sayaç artır
                                sutunOzeti[i].Nullable = true;
                                sutunOzeti[i].BosDegerSayisi++;
                            }
                            else
                            {
                                // 👉 boş değilse: tipi keşfet ve mevcutla birleştir
                                sutunOzeti[i].TanimlananTur =
                                    MergeType(sutunOzeti[i].TanimlananTur, InferType(deger));
                            }
                        }

                        satirSayisi++;
                    }
                }

                // ---------- 2) Tabloyu oluştur (idempotent) ----------
                // 👉 Keşfedilen kolon tiplerine göre CREATE TABLE IF NOT EXISTS
                await CreateTargetTableAsync(conn, _schema, table, sutunOzeti, ct);


                // ---------- 3) İkinci geçiş: COPY BINARY ile hızlı yaz ----------
                // 👉 Şimdi aynı dosyayı tekrar okuyup gerçek veriyi tablonun kolonlarına basıyoruz
                using (var sr2 = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                {
                    // 👉 header'ı atla
                    await sr2.ReadLineAsync();

                    var kolonCount = sutunOzeti.Length;
                    string kolonListSql = string.Join(", ",
         sutunOzeti.Select(s => QuoteIdent(s.Ad))
         .Concat(new[]
         {
            QuoteIdent("batch_id"),
            QuoteIdent("data_date")
         }));

                    await using var importer = await conn.BeginBinaryImportAsync(
                        $"COPY {QuoteIdent(_schema)}.{QuoteIdent(table)} ({kolonListSql}) FROM STDIN (FORMAT BINARY)", ct);

                    string? line;
                    while ((line = await sr2.ReadLineAsync()) != null)
                    {
                        ct.ThrowIfCancellationRequested();

                        var values = SplitCsvLine(line).ToArray();

                        await importer.StartRowAsync(ct);

                        // 1) CSV'den gelen kolonlar
                        for (int i = 0; i < kolonCount; i++)
                        {
                            string? deger = i < values.Length ? values[i] : null;
                            var tip = sutunOzeti[i].TanimlananTur;

                            if (string.IsNullOrEmpty(deger))
                            {
                                importer.WriteNull();
                                continue;
                            }

                            var (obj, npgsqlType) = ConvertToPgValue(deger, tip);
                            importer.Write(obj, npgsqlType);
                        }

                        // 2) Ek kolonlar: batch_id + data_date
                        importer.Write(batchId, NpgsqlDbType.Uuid);
                        importer.Write(effectiveDataDate, NpgsqlDbType.Date);
                    }

                    await importer.CompleteAsync(ct);

                }

                // 👉 Sonuç özetini doldur
                sonuc.AktarilanTabloSayisi++;
                sonuc.ToplamSatirSayisi += satirSayisi;
                sonuc.Tablolar.Add(new TabloOzeti
                {
                    TabloAdi = $"{_schema}.{table}",
                    SatirSayisi = satirSayisi,
                    Sutunlar = sutunOzeti.ToList()
                });
            }

            return sonuc;
        }

        // ======================
        // ŞEMA & TABLO OLUŞTURMA
        // ======================

        private static async Task EnsureSchemaAsync(NpgsqlConnection conn, string schema, CancellationToken ct)
        {
            // 👉 Şema yoksa oluştur
            await using var cmd = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {QuoteIdent(schema)};", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task ResetSchemaAsync(NpgsqlConnection conn, string schema, CancellationToken ct)
        {
            // Şemayı komple silip tekrar oluşturur.
            var sql = $@"
DROP SCHEMA IF EXISTS {QuoteIdent(schema)} CASCADE;
CREATE SCHEMA {QuoteIdent(schema)};";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task CreateTargetTableAsync(
            NpgsqlConnection conn, string schema, string table,
            SutunOzeti[] sutunlar, CancellationToken ct)
        {
            var colsSql = string.Join(", ",
                sutunlar.Select(s => $"{QuoteIdent(s.Ad)} {PgTypeFor(s)}"));
            var extraCols = @",
    batch_id uuid,
    data_date date";

            var sql = $@"
CREATE SCHEMA IF NOT EXISTS {QuoteIdent(schema)};



CREATE TABLE IF NOT EXISTS {QuoteIdent(schema)}.{QuoteIdent(table)}
(
    {colsSql}{extraCols}
);";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        private static string PgTypeFor(SutunOzeti s)
        {
            // 👉 Keşfedilen tip → PostgreSQL tipi
            // string için text; int→bigint; decimal→numeric(38,10); bool→boolean; date→date; datetime→timestamptz
            var pgType = s.TanimlananTur switch
            {
                "int" => "bigint",
                "decimal" => "numeric(38,10)",
                "bool" => "boolean",
                "date" => "date",
                "datetime" => "timestamptz",
                _ => "text"
            };
            string nullability = s.Nullable ? "NULL" : "NOT NULL";
            return $"{pgType} {nullability}";
        }

        // ======================
        // VERİ DÖNÜŞÜMÜ (string -> .NET objesi + NpgsqlDbType)
        // ======================
        private static (object value, NpgsqlDbType type) ConvertToPgValue(string deger, string tip)
        {
            // 👉 COPY BINARY için string değeri doğru .NET tipi + NpgsqlDbType ile döndürür
            switch (tip)
            {
                case "bool":
                    if (bool.TryParse(deger, out var b))
                        return (b, NpgsqlDbType.Boolean);
                    // Parse olmazsa güvenli default
                    return (false, NpgsqlDbType.Boolean);

                case "int":
                    if (long.TryParse(deger, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        return (l, NpgsqlDbType.Bigint);
                    if (long.TryParse(deger, NumberStyles.Integer, new CultureInfo("tr-TR"), out l))
                        return (l, NpgsqlDbType.Bigint);
                    return (DBNull.Value, NpgsqlDbType.Bigint);

                case "decimal":
                    if (decimal.TryParse(deger, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                        return (d, NpgsqlDbType.Numeric);
                    if (decimal.TryParse(deger, NumberStyles.Number, new CultureInfo("tr-TR"), out d))
                        return (d, NpgsqlDbType.Numeric);
                    return (DBNull.Value, NpgsqlDbType.Numeric);

                case "date":
                    // Sadece tarih (saat 00:00). DB tarafında "date" kolonuna gider.
                    if (string.IsNullOrWhiteSpace(deger))
                        return (DBNull.Value, NpgsqlDbType.Date);

                    if (DateOnly.TryParse(deger, CultureInfo.InvariantCulture, out var dateOnly))
                        return (dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                                NpgsqlDbType.Date);

                    if (DateTime.TryParse(deger, CultureInfo.InvariantCulture,
                                          DateTimeStyles.None,
                                          out var dtDate))
                        return (dtDate.Date, NpgsqlDbType.Date);

                    return (DBNull.Value, NpgsqlDbType.Date);

                case "datetime":
                    if (string.IsNullOrWhiteSpace(deger))
                        return (DBNull.Value, NpgsqlDbType.TimestampTz);

                    // 1) Önce invariant kültürle dene (ISO tarzı formatlar için)
                    if (!DateTime.TryParse(
                            deger,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var dt)
                        // 2) Olmazsa Türkçe kültürle dene
                        && !DateTime.TryParse(
                            deger,
                            new CultureInfo("tr-TR"),
                            DateTimeStyles.AssumeLocal,
                            out dt))
                    {
                        // Hiçbir formatla parse edemezsek NULL yaz
                        return (DBNull.Value, NpgsqlDbType.TimestampTz);
                    }

                    // 👉 Burada Kind'ı kesinleştiriyoruz ki Npgsql şikâyet etmesin
                    if (dt.Kind == DateTimeKind.Unspecified)
                    {
                        // Timezone bilgisi yoksa "bu zaten UTC" diye işaretle
                        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    }
                    else
                    {
                        // Local veya Utc ise → net olarak UTC'ye çevir
                        dt = dt.ToUniversalTime();
                    }

                    return (dt, NpgsqlDbType.TimestampTz);

                default:
                    // Diğer her şey TEXT olarak yazılsın
                    return (deger, NpgsqlDbType.Text);
            }
        }
        // ======================
        // CSV & TİP KEŞFİ YARDIMCILARI
        // ======================

        private static IEnumerable<string?> SplitCsvLine(string line)
        {
            // 👉 CSV ayrıştırıcı: hem ',' hem ';' ayırıcı olarak kabul eder.
            // Çift tırnak içindeki ayırıcıları dikkate almaz.
            // "" → " kaçışını destekler.

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '\"')
                {
                    // Kaçış durumu: "" → "
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        sb.Append('\"');
                        i++; // Bir adım atla
                    }
                    else
                    {
                        inQuotes = !inQuotes; // Tırnak aç/kapa
                    }
                }
                else if ((c == ',' || c == ';') && !inQuotes)
                {
                    // Ayırıcıya geldik → bir kolon tamamlandı
                    yield return sb.Length == 0 ? null : sb.ToString();
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            // Son kolon
            yield return sb.Length == 0 ? null : sb.ToString();
        }


        private static string InferType(string value)
        {
            // 👉 Tek bir hücrenin tipini keşfet
            if (bool.TryParse(value, out _)) return "bool";
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return "int";
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return "decimal";
            if (decimal.TryParse(value, NumberStyles.Number, new CultureInfo("tr-TR"), out _)) return "decimal";

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                if (dt.TimeOfDay == TimeSpan.Zero) return "date";
                return "datetime";
            }
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _)) return "date";

            return "string";
        }

        private static string MergeType(string current, string incoming)
        {
            // 👉 Sütunun mevcut tipi ile yeni gelen tip tahminini birleştir
            if (current == "unknown")
                return incoming;
            if (incoming == "unknown")
                return current;
            if (current == incoming) return current;

            // int + decimal => decimal
            if ((current == "int" && incoming == "decimal") || (current == "decimal" && incoming == "int"))
                return "decimal";

            // date + datetime => datetime
            if ((current == "date" && incoming == "datetime") || (current == "datetime" && incoming == "date"))
                return "datetime";

            // Diğer uyumsuz kombinasyonlarda güvenli tip: string
            var set = new HashSet<string> { current, incoming };
            if (set.SetEquals(new[] { "int", "decimal" })) return "decimal";
            if (set.SetEquals(new[] { "date", "datetime" })) return "datetime";

            return "string";
        }

        // ======================
        // İSİM TEMİZLEME & QUOTE
        // ======================

        private static string SanitizeIdentifier(string raw)
        {
            // 👉 TR karakterleri ascii'ye çevir; boşluk/eksi -> '_' ; başı rakamsa '_' ekle
            var map = new Dictionary<char, char>
            {
                ['ç'] = 'c',
                ['ğ'] = 'g',
                ['ı'] = 'i',
                ['ö'] = 'o',
                ['ş'] = 's',
                ['ü'] = 'u',
                ['Ç'] = 'C',
                ['Ğ'] = 'G',
                ['İ'] = 'I',
                ['Ö'] = 'O',
                ['Ş'] = 'S',
                ['Ü'] = 'U'
            };
            var sb = new StringBuilder();
            foreach (var ch in raw.Trim())
            {
                char c = map.TryGetValue(ch, out var repl) ? repl : ch;
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (char.IsWhiteSpace(c) || c == '-') sb.Append('_');
            }
            var s = sb.ToString();
            if (string.IsNullOrEmpty(s)) s = "kolon";
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        private static string QuoteIdent(string ident) => $"\"{ident}\""; // 👉 Postgres identifier'ı güvenli tırnakla

        // ============================================================
        // 3. ADIM: DİNAMİK TABLO FİLTRELEME MANTIĞI (YENİ HELPER)
        // ============================================================
        /// <summary>
        /// Bir CSV tablosunun veritabanında ayrı bir tablo olarak tutulmasının gereksiz
        /// olup olmadığını dinamik olarak yorumlar.
        ///
        /// Mantık:
        ///   - Örn: person_accounts_account_transactions_transaction_amount
        ///   - Base tablo: person_accounts_account_transactions_transaction
        ///   - Eğer:
        ///       * base tablo gerçekten varsa,
        ///       * base tabloda "amount" isminde bir kolon varsa,
        ///       * küçük tablonun kolonları da "fk + generic sutun/value/code" gibiyse
        ///     → Bu tabloyu "aynı alanın kırığı" sayıp DB'de oluşturmaya gerek yok.
        /// </summary>
        private static bool TabloDbIcinGereksiz(
            string? rawTableName,
            IReadOnlyDictionary<string, string[]> headerHaritasi)
        {
            if (string.IsNullOrWhiteSpace(rawTableName))
                return false;

            var lower = rawTableName.ToLowerInvariant();

            // İsim parçalarına ayır
            var parts = lower.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            // Base tablo adı: sondan 1 segment eksik
            var baseName = string.Join('_', parts[..^1]);
            var alanAdi = parts[^1]; // son segment: amount, date, description, vs.

            // Base tablo gerçekten var mı?
            if (!headerHaritasi.TryGetValue(baseName, out var baseHeader))
                return false;

            // Küçük tablonun header'ı var mı?
            if (!headerHaritasi.TryGetValue(rawTableName, out var childHeader))
                return false;

            // Base tablo kolon adları
            var baseCols = new HashSet<string>(
                baseHeader
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim().ToLowerInvariant())
            );

            // Base tabloda bu isimde kolon yoksa → ilişki zayıf, riske girmeyelim
            if (!baseCols.Contains(alanAdi))
                return false;

            // Küçük tablonun kolonları
            var childCols = childHeader
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .ToArray();

            // Çok kolon varsa bu muhtemelen gerçek bir entity'dir, atlamayalım
            if (childCols.Length > 3)
                return false;

            // FK / id dışındaki anlamlı kolonlara bakalım
            var degerKolonlari = childCols
                .Where(c =>
                    c != "id" &&
                    !c.EndsWith("_fk") &&
                    c != "personlist_person_fk" &&
                    c != "sutun" &&
                    c != "value" &&
                    c != "code")
                .ToArray();

            // Eğer anlamlı ekstra kolon yoksa → bu tablo base tablodaki alanAdi'nın
            // gereksiz kırığıdır, DB'de tabloya dönüştürmeye gerek yok.
            if (degerKolonlari.Length == 0)
                return true;

            return false;
        }

        // ============================================================
        // YARDIMCI: OLUŞAN TABLOLARI LİSTELEME
        // ============================================================
        public async Task OlusanTablolariTerminaleYazdirAsync()
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connStr);
                await conn.OpenAsync();

                // Sadece bizim şemamıza (_schema) ait tabloları isim sırasına göre çek
                var sql = @"
                    SELECT table_name 
                    FROM information_schema.tables 
                    WHERE table_schema = @schema 
                    ORDER BY table_name;";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("schema", _schema);

                using var reader = await cmd.ExecuteReaderAsync();

                Console.WriteLine();
                Console.WriteLine($"╔════════════════════════════════════════════════════╗");
                Console.WriteLine($"║   VERİTABANINDAKİ MEVCUT TABLOLAR ({_schema})   ║");
                Console.WriteLine($"╚════════════════════════════════════════════════════╝");

                int sayac = 0;
                while (await reader.ReadAsync())
                {
                    sayac++;
                    var tabloAdi = reader.GetString(0);
                    // Terminalde şık görünmesi için:
                    Console.WriteLine($"  {sayac,3}. {tabloAdi}");
                }

                if (sayac == 0)
                {
                    Console.WriteLine("  -> Hiç tablo bulunamadı.");
                }

                Console.WriteLine("------------------------------------------------------");
                Console.WriteLine($"  TOPLAM: {sayac} adet tablo mevcut.");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Tablo listesi alınırken hata: {ex.Message}");
                Console.ResetColor();
            }
        }
        private static async Task EnsureImportBatchesTableAsync(
    NpgsqlConnection conn,
    CancellationToken ct)
        {
            var sql = @"
CREATE TABLE IF NOT EXISTS import_batches (
    id uuid PRIMARY KEY,
    source_file_name text NOT NULL,
    load_type text NOT NULL,
    data_date date NOT NULL,
    loaded_at timestamptz NOT NULL DEFAULT now(),
    record_count int
);";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task KaydetImportBatchAsync(
            NpgsqlConnection conn,
            Guid batchId,
            string sourceFileName,
            string loadType,
            DateTime dataDate,
            CancellationToken ct)
        {
            var sql = @"
INSERT INTO import_batches (id, source_file_name, load_type, data_date)
VALUES (@id, @src, @type, @date);";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", batchId);
            cmd.Parameters.AddWithValue("src", sourceFileName);
            cmd.Parameters.AddWithValue("type", loadType);
            cmd.Parameters.AddWithValue("date", dataDate);
            await cmd.ExecuteNonQueryAsync(ct);
        }

    }
}