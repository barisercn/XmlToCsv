// Services/XmlKesifci.cs - Tüm Metotları Doldurulmuş Nihai Sürüm

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using XmlCsvMini.Altyapi;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    public sealed class XmlKesifci
    {
        public KesifRaporu Kesfet(
            string xmlYolu,
            int ornekSayisi = 50_000_000,
            int maksDerinlik = 15,
            int adayMinTekrar = 5,
            int alanOrnekLimit = 5)
        {
            if (!File.Exists(xmlYolu)) throw new FileNotFoundException("XML dosyası bulunamadı.", xmlYolu);

            var rapor = new KesifRaporu { Kaynak = xmlYolu };
            var yolSayaci = new Dictionary<string, long>();
            var ebeveynAltAlanlari = new Dictionary<string, HashSet<string>>();
            var ebeveynIcindekiDiziSayaci = new Dictionary<(string ebeveyn, string rel), int>();
            var alanOrnekleri = new Dictionary<(string ebeveyn, string rel), List<string>>();
            var alanFarkliDeger = new Dictionary<(string ebeveyn, string rel), HashSet<string>>();
            var adUzaylari = new Dictionary<string, string>();

            var ayarlar = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore, CloseInput = true };
            var yigin = new Stack<ElemanBaglami>();
            long tarananDugum = 0;
            bool adUzayiToplandi = false;

            using (var okuyucu = EncodingAwareXml.CreateAutoXmlReader(xmlYolu, ayarlar))
            {
                while (okuyucu.Read())
                {
                    if (tarananDugum >= ornekSayisi) break;
                    switch (okuyucu.NodeType)
                    {
                        case XmlNodeType.Element:
                            {
                                tarananDugum++;

                                if (yigin.Count >= maksDerinlik)
                                {
                                    okuyucu.Skip();
                                    tarananDugum++;
                                    continue;
                                }

                                var simdikiYol = YolOlustur(yigin.Select(s => s.Ad).Reverse().ToList(), okuyucu.LocalName);

                                yolSayaci.TryAdd(simdikiYol, 0);
                                yolSayaci[simdikiYol]++;

                                if (!adUzayiToplandi && okuyucu.HasAttributes)
                                {
                                    for (int i = 0; i < okuyucu.AttributeCount; i++)
                                    {
                                        okuyucu.MoveToAttribute(i);
                                        if (okuyucu.Prefix == "xmlns") adUzaylari[okuyucu.LocalName] = okuyucu.Value;
                                        else if (okuyucu.Name == "xmlns") adUzaylari[""] = okuyucu.Value;
                                    }
                                    okuyucu.MoveToElement();
                                    adUzayiToplandi = true;
                                }

                                var ebeveynYolu = yigin.Count > 0 ? yigin.Peek().Yol : null;
                                yigin.Push(new ElemanBaglami(okuyucu.LocalName, simdikiYol, ebeveynYolu));

                                if (okuyucu.HasAttributes)
                                {
                                    if (!EbeveynKumesi(simdikiYol, ebeveynAltAlanlari, out var kume)) { }
                                    for (int i = 0; i < okuyucu.AttributeCount; i++)
                                    {
                                        okuyucu.MoveToAttribute(i);
                                        if (okuyucu.Prefix == "xmlns" || okuyucu.Name == "xmlns") continue;
                                        var rel = "./@" + okuyucu.LocalName;
                                        kume.Add(rel);
                                        OrnekEkle((simdikiYol, rel), okuyucu.Value);
                                        DiziSay(simdikiYol, rel);
                                    }
                                    okuyucu.MoveToElement();
                                }

                                if (okuyucu.IsEmptyElement)
                                {
                                    HandleEndElement();
                                }

                                break;
                            }
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                            {
                                tarananDugum++;
                                if (yigin.Count > 0)
                                {
                                    yigin.Peek().MetinBirikimi.Append(okuyucu.Value);
                                }
                                break;
                            }
                        case XmlNodeType.EndElement:
                            {
                                tarananDugum++;
                                HandleEndElement();
                                break;
                            }
                    }
                }
            }

            void HandleEndElement()
            {
                if (yigin.Count == 0) return;
                var kapanan = yigin.Pop();
                var metin = kapanan.MetinBirikimi.Length > 0 ? kapanan.MetinBirikimi.ToString().Trim() : null;
                var ebeveynYolu = kapanan.EbeveynYolu;

                if (!string.IsNullOrEmpty(ebeveynYolu))
                {
                    if (!EbeveynKumesi(ebeveynYolu, ebeveynAltAlanlari, out var kume)) { }
                    var rel = "./" + kapanan.Ad;
                    kume.Add(rel);
                    DiziSay(ebeveynYolu, rel);
                    if (!string.IsNullOrEmpty(metin)) OrnekEkle((ebeveynYolu, rel), metin);
                }

                if (!string.IsNullOrEmpty(metin))
                {
                    if (!EbeveynKumesi(kapanan.Yol, ebeveynAltAlanlari, out var kendiKumesi)) { }
                    var metinYolu = "./text()";
                    kendiKumesi.Add(metinYolu);
                    DiziSay(kapanan.Yol, metinYolu);
                    OrnekEkle((kapanan.Yol, metinYolu), metin);
                }
            }

            rapor.AdUzaylari = new SozlukAdUzayi(adUzaylari);

            // =========================================================================
            // === DÜZELTME: Hatalı gruplama ve filtreleme mantığı kaldırıldı. =========
            // Artık, minimum tekrar sayısını geçen ve en az bir alt alanı olan TÜM yollar
            // aday olarak kabul edilecek. Bu, /PFA/Records/Entity gibi ebeveynlerin
            // rapordan atılmasını engelleyecektir.
            // =========================================================================
            var adayYollar = yolSayaci
                .Where(kv => kv.Value >= adayMinTekrar && ebeveynAltAlanlari.TryGetValue(kv.Key, out var set) && set.Count > 0)
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var kayitYolu in adayYollar)
            {
                var alanlar = new List<AlanBilgisi>();
                var diziler = new List<IcIceDizi>();
                if (!ebeveynAltAlanlari.TryGetValue(kayitYolu, out var alanKumesi)) continue;

                foreach (var rel in alanKumesi.OrderBy(s => s))
                {
                    var diziMi = ebeveynIcindekiDiziSayaci.TryGetValue((kayitYolu, rel), out var say) && say > 1;
                    var kard = diziMi ? "0..N" : "0..1";

                    List<string>? ornekler = null;
                    if (alanOrnekleri.TryGetValue((kayitYolu, rel), out var liste)) ornekler = liste;

                    var cikarilanTur = TuruCikar(ornekler ?? Enumerable.Empty<string>());
                    var dugumTuru = rel.StartsWith("./@") ? DugumTuru.Attribute : (rel.Contains("text()") ? DugumTuru.Text : DugumTuru.Element);

                    int? maks = null;
                    int? yaklasikFarkli = null;
                    if (ornekler != null && ornekler.Count > 0)
                    {
                        maks = ornekler.Max(s => s?.Length ?? 0);
                        if (alanFarkliDeger.TryGetValue((kayitYolu, rel), out var hs)) yaklasikFarkli = hs.Count;
                    }

                    alanlar.Add(new AlanBilgisi
                    {
                        Yol = rel,
                        Tur = dugumTuru,
                        Kardinalite = kard,
                        Ornekler = (ornekler ?? new List<string>()),
                        CikarilanTur = cikarilanTur,
                        MaksKarakterUzunlugu = maks,
                        YaklasikFarkliDegerSayisi = yaklasikFarkli
                    });

                    if (diziMi && dugumTuru == DugumTuru.Element)
                    {
                        diziler.Add(new IcIceDizi { TekrarYolu = rel, OnerilenTablo = OnerilenTabloAdi(kayitYolu, rel) });
                    }
                }

                rapor.AdayKayitlar.Add(new AdayKayit
                {
                    KayitYolu = kayitYolu,
                    TahminiKayitSayisi = yolSayaci[kayitYolu],
                    Alanlar = alanlar,
                    IcIceDiziler = diziler
                });
            }

            rapor.Bilgi = new KesifMeta
            {
                UretimZamaniUtc = DateTime.UtcNow,
                ParametreOzeti = $"sample={ornekSayisi}; depth={maksDerinlik}; minRepeat={adayMinTekrar}",
                TarananDugumSayisi = tarananDugum
            };

            return rapor;

            // ----- yardımcılar -----
            bool EbeveynKumesi(string e, Dictionary<string, HashSet<string>> m, out HashSet<string> k) { if (!m.TryGetValue(e, out k!)) { k = new HashSet<string>(); m[e] = k; return false; } return true; }
            void OrnekEkle((string e, string r) k, string? v) { if (string.IsNullOrWhiteSpace(v)) return; if (!alanOrnekleri.TryGetValue(k, out var l)) { l = new List<string>(); alanOrnekleri[k] = l; } if (l.Count < alanOrnekLimit) l.Add(v); if (!alanFarkliDeger.TryGetValue(k, out var h)) { h = new HashSet<string>(); alanFarkliDeger[k] = h; } if (h.Count < 1000) h.Add(v); }
            void DiziSay(string e, string r) { var k = (e, r); ebeveynIcindekiDiziSayaci.TryAdd(k, 0); ebeveynIcindekiDiziSayaci[k]++; }
            static string YolOlustur(List<string> y, string s) { if (y.Count == 0) return "/" + s; var sb = new StringBuilder(); foreach (var p in y) sb.Append('/').Append(p); sb.Append('/').Append(s); return sb.ToString(); }
            static string OnerilenTabloAdi(string e, string r) { var p = e.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "P"; var c = r.Replace("./", ""); return $"{p}_{c}"; }
            static CikarilanVeriTuru TuruCikar(IEnumerable<string> o) { bool i = true, d = true, b = true, dt = true, dtm = true; foreach (var s in o.Where(s => !string.IsNullOrWhiteSpace(s))) { if (i && !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) i = false; if (d && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) d = false; if (b && !BoolMu(s)) b = false; if (dt && !TarihMi(s)) dt = false; if (dtm && !TarihSaatMi(s)) dtm = false; if (!i && !d && !b && !dt && !dtm) break; } if (i) return CikarilanVeriTuru.Integer; if (d) return CikarilanVeriTuru.Decimal; if (b) return CikarilanVeriTuru.Boolean; if (dtm) return CikarilanVeriTuru.DateTime; if (dt) return CikarilanVeriTuru.Date; return CikarilanVeriTuru.String; static bool BoolMu(string s) { var t = s.ToLowerInvariant(); return t is "true" or "false" or "1" or "0" or "y" or "n"; } static bool TarihMi(string s) { return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.TimeOfDay == TimeSpan.Zero; } static bool TarihSaatMi(string s) { return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _); } }
        }

        private sealed class ElemanBaglami
        {
            public string Ad { get; }
            public string Yol { get; }
            public string? EbeveynYolu { get; }
            public StringBuilder MetinBirikimi { get; } = new();

            public ElemanBaglami(string ad, string yol, string? ebeveynYolu = null)
            {
                Ad = ad; Yol = yol; EbeveynYolu = ebeveynYolu;
            }
        }
    }
}