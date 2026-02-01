// Temporary test runner - DELETE AFTER TESTING
using XmlCsvMini.Services;

public static class TestRunner
{
    public static void Run(string xmlPath, string outputDir)
    {
        if (string.IsNullOrEmpty(xmlPath)) return;
        Console.WriteLine($"=== TEST: {xmlPath} ===");
        SmartXmlToCsvExporter.Calistir(xmlPath, outputDir);

        // List generated CSV files and their headers
        Console.WriteLine("\n=== GENERATED CSV FILES ===");
        foreach (var f in Directory.GetFiles(outputDir, "*.csv").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var firstLine = File.ReadLines(f).FirstOrDefault() ?? "(empty)";
            var lineCount = File.ReadLines(f).Count() - 1; // minus header
            Console.WriteLine($"\n📁 {name} ({lineCount} rows)");
            Console.WriteLine($"   Columns: {firstLine}");
        }
    }
}
