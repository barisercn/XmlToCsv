using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XmlCsvMini.Models
{
    /// <summary>
    /// "Smart" veya "Sematik" XML ayrıştırıcısı için yapılandırma.
    /// Sabit kodlanmış kuralları dışarıdan yönetilebilir hale getirir.
    /// </summary>
    public class SematikEsleme
    {
        /// <summary>
        /// Bir eleman adının bu soneklerden biriyle bitmesi, onun bir "container" (kapsayıcı)
        /// olduğunu ve tablo adlandırmada kök adının kullanılacağını belirtir.
        /// Örn: "Details" -> "NameDetails" adı "name" olarak sadeleşir.
        /// Varsayılan: ["List", "Types", "References", "Details"]
        /// </summary>
        [JsonPropertyName("containerSuffixes")]
        public List<string> ContainerSuffixes { get; set; } = new();

        /// <summary>
        /// Bu elemanlar, özel "anchor" (çapa) davranışı sergiler. Görüldükleri yerde,
        /// hem kendi alt elemanlarını hem de niteliklerini, hem de bir üst ebeveyninin
        /// niteliklerini tek bir tabloda birleştirirler (düzleştirirler).
        /// Varsayılan: ["NameValue"]
        /// </summary>
        [JsonPropertyName("anchorElements")]
        public List<string> AnchorElements { get; set; } = new();

        /// <summary>
        /// Bu isimdeki elemanların altındaki tablolara `entity_id` sütunu eklenir.
        /// Bu, veritabanında ana kayıtlarla ilişki kurmayı sağlar.
        /// Varsayılan: ["Entity", "SpecialEntity"]
        /// </summary>
        [JsonPropertyName("mainRecordNodeNames")]
        public List<string> MainRecordNodeNames { get; set; } = new();

        /// <summary>
        /// Tablo adı oluşturulurken eleman adlarının sonundan kaldırılacak genel sonekler.
        /// Örn: "NameValue" elemanı "Name" tablosuna dönüşür.
        /// Varsayılan: ["Value"]
        /// </summary>
        [JsonPropertyName("suffixesToRemoveFromTableNames")]
        public List<string> SuffixesToRemoveFromTableNames { get; set; } = new();
    }
}
