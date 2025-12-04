// Altyapi/TableNameBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XmlCsvMini.Altyapi
{
    /// <summary>
    /// XML absolut path'lerinden mantıklı ve tutarlı tablo adları üretir.
    /// Örn:
    ///   rootPath:  /Root/PersonList/Person
    ///   recordPath:/Root/PersonList/Person/Addresses/Address/Geo/Lat
    /// -> person_addresses_address_geo_lat
    /// </summary>
    public static class TableNameBuilder
    {
        /// <summary>
        /// Ana kayıt yolu (rootPath) ve ilgili kayıt yolu (recordPath) kullanılarak
        /// tablo adı üretir.
        /// </summary>
        public static string BuildTableName(string rootPath, string recordPath)
        {
            if (string.IsNullOrWhiteSpace(recordPath))
                return "bilinmeyen_kayit";

            var rootParts = SplitPath(rootPath);
            var recParts = SplitPath(recordPath);

            if (recParts.Count == 0)
                return "bilinmeyen_kayit";

            // ROOT TABLO: recordPath == rootPath veya root'un altına ekstra segment yoksa
            if (PathsEqual(rootParts, recParts) || recParts.Count <= rootParts.Count)
            {
                // Root tablolarda son 2 segmenti kullan: PersonList_Person, CompanyList_Company vs.
                var rootNameParts = rootParts
                    .Skip(Math.Max(0, rootParts.Count - 2))
                    .Select(NormalizeSegment)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                if (rootNameParts.Count == 0)
                    rootNameParts.Add("root");

                return string.Join('_', rootNameParts);
            }

            // ALT TABLOLAR:
            // 1) Root ile ortak başlangıcı bul (prefix)
            int common = LongestCommonPrefixLength(rootParts, recParts);

            // 2) Rootun KAYIT tipi (Person, Company, Event) -> sadece SON segment
            // Örn: /Root/PersonList/Person -> "person"
            var rootRecordName = rootParts.LastOrDefault() ?? "root";
            rootRecordName = NormalizeSegment(rootRecordName);

            // 3) Geri kalan RELATIVE path segmentlerini al
            // Örn: Addresses/Address/Geo/Lat -> ["Addresses","Address","Geo","Lat"]
            var relativeParts = recParts
                .Skip(common)
                .Select(NormalizeSegment)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // 4) Tablo adını birleştir: person_addresses_address_geo_lat
            var allParts = new List<string> { rootRecordName };
            allParts.AddRange(relativeParts);

            return string.Join('_', allParts);
        }

        /// <summary>
        /// "/Root/PersonList/Person/Addresses/Address" gibi path'i
        /// ["Root","PersonList","Person","Addresses","Address"] listesini çevirir.
        /// </summary>
        private static List<string> SplitPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new List<string>();

            return path
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static bool PathsEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static int LongestCommonPrefixLength(List<string> a, List<string> b)
        {
            int len = Math.Min(a.Count, b.Count);
            int i = 0;
            for (; i < len; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    break;
            }
            return i;
        }

        /// <summary>
        /// Segment içindeki namespace, Türkçe karakterler, boşluklar vb. temizlenir.
        /// Örn: "ext:Social" -> "social", "Adres Bilgisi" -> "adres_bilgisi"
        /// </summary>
        private static string NormalizeSegment(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
                return "";

            // "ext:Social" -> "Social"
            var colonIndex = segment.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < segment.Length - 1)
                segment = segment[(colonIndex + 1)..];

            segment = segment.Trim();

            // Türkçe karakterleri sadeleştir
            segment = ReplaceTurkishChars(segment);

            // Harf/rakam/digerlerini normalize et
            var sb = new StringBuilder(segment.Length);
            foreach (var ch in segment)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    // diğer her şey (boşluk, tire, nokta, vb.) -> '_'
                    sb.Append('_');
                }
            }

            // Birden fazla '_' yan yana gelmişse teke indir
            var normalized = sb.ToString();
            while (normalized.Contains("__"))
                normalized = normalized.Replace("__", "_");

            // Baştaki/sondaki '_' lerden kurtul
            normalized = normalized.Trim('_');

            return normalized;
        }

        private static string ReplaceTurkishChars(string input)
        {
            // Basit Türkçe -> Latin sadeleştirme
            return input
                .Replace('Ç', 'C').Replace('ç', 'c')
                .Replace('Ğ', 'G').Replace('ğ', 'g')
                .Replace('İ', 'I').Replace('ı', 'i')
                .Replace('Ö', 'O').Replace('ö', 'o')
                .Replace('Ş', 'S').Replace('ş', 's')
                .Replace('Ü', 'U').Replace('ü', 'u');
        }
    }
}
