using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XmlCsvMini.Models
{
    public interface IVeriAktarimServisi
    {
        Task<VeriAktarimSonucu> ZiptenVeritabaninaAktarAsync(string zipYolu, CancellationToken ct = default);
    }

    public sealed class VeriAktarimSonucu
    {
        public int AktarilanTabloSayisi { get; set; }
        public long ToplamSatirSayisi { get; set; }
        public List<TabloOzeti> Tablolar { get; set; } = new();
    }

    public sealed class TabloOzeti
    {
        public string TabloAdi { get; set; } = "";
        public long SatirSayisi { get; set; }
        public List<SutunOzeti> Sutunlar { get; set; } = new();
    }

    public sealed class SutunOzeti
    {
        public string Ad { get; set; } = "";
        public string TanimlananTur { get; set; } = "unknown"; // string,int,decimal,bool,date,datetime
        public bool Nullable { get; set; }
        public long BosDegerSayisi { get; set; }
    }
}
