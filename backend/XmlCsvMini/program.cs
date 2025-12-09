using Microsoft.AspNetCore.Http.Features;   // FormOptions için
using XmlCsvMini.Services;
using XmlCsvMini.Models;

var builder = WebApplication.CreateBuilder(args);

// 💡 Kestrel web sunucusunun kabul edeceği maksimum istek gövdesi (body) boyutu.
// Varsayılan limit (~30 MB) büyük dosyalar için yetersiz.
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // 10 GB = 10 * 1024 * 1024 * 1024 bayt.
    serverOptions.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024;
});

// 💡 ASP.NET Core'un form/multipart parse ederken kullandığı limitler.
builder.Services.Configure<FormOptions>(options =>
{
    // Multipart (dosya upload) için 10 GB limit
    options.MultipartBodyLengthLimit = 10L * 1024 * 1024 * 1024;

    // Form alanları (text vs.) için uzunluk limitleri
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Frontend'den gelen isteklere izin vermek için CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

// Controller'ları (UploadController vs.) ekliyoruz.
builder.Services.AddControllers();
builder.Services.AddScoped<IVeriAktarimServisi, VeriAktarimServisi>();
builder.Services.AddSingleton<IIslemeGoreviYonetimi, InMemoryIslemeGoreviYonetimi>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors();
app.MapControllers();
app.Run();
