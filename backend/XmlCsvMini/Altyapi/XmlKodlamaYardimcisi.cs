using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace XmlCsvMini.Altyapi
{
    public static class EncodingAwareXml
    {
        // .NET 6/7/8'de gerekliyse Program.Main'de RegisterProvider çağrısını da yapacağız.
        public static XmlReader CreateAutoXmlReader(string path, XmlReaderSettings settings)
        {
            // 1) Dosyayı aç
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 2) İlk 4KB’yi ASCII olarak oku ve XML deklarasyonunda encoding var mı bak
            var buf = new byte[4096];
            int read = fs.Read(buf, 0, buf.Length);
            fs.Position = 0; // geri sar

            var header = Encoding.ASCII.GetString(buf, 0, read);
            var m = Regex.Match(header,
                "<\\?xml[^>]*encoding=['\"](?<enc>[^'\"\\s>]+)['\"]",
                RegexOptions.IgnoreCase);

            if (m.Success)
            {
                var encName = m.Groups["enc"].Value.Trim();
                // Belirtilen kodlama hatalıysa yakala ve aşağıdaki akışa düş
                try
                {
                    var enc = Encoding.GetEncoding(encName);
                    var tr = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
                    return XmlReader.Create(tr, settings);
                }
                catch
                {
                    // devame edip alttaki denemelere bırak
                }
            }

            // 3) Deklarasyon yoksa (veya geçersizse): XmlReader’a bırak (BOM varsa algılar; yoksa UTF-8 kabul eder)
            try
            {
                return XmlReader.Create(fs, settings);
            }
            catch (XmlException)
            {
                // 4) Son çare: Türkçe için makul fallback ile tekrar dene (legacy XML'ler)
                fs.Dispose();
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var encTr = Encoding.GetEncoding("windows-1254"); // veya "iso-8859-9"
                var tr = new StreamReader(fs, encTr, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: false);
                return XmlReader.Create(tr, settings);
            }
        }
    }
}