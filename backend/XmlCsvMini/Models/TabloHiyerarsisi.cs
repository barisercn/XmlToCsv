using System.Collections.Generic;

namespace XmlCsvMini.Models
{
    /// <summary>
    /// Bir ana tablo adayını ve ona hiyerarşik olarak bağlı alt tablo adaylarını temsil eder.
    /// </summary>
    public class TabloHiyerarsisi
    {
        public AdayKayit AnaTablo { get; set; }
        public List<AdayKayit> AltTablolar { get; set; }

        public TabloHiyerarsisi(AdayKayit anaTablo)
        {
            AnaTablo = anaTablo;
            AltTablolar = new List<AdayKayit>();
        }
    }
}