var builder = WebApplication.CreateBuilder(args);

// Frontend'den (React uygulamasından) gelen isteklere izin vermek için CORS politikası ekliyoruz.
// React/Vite'in varsayılan geliştirme adresi "http://localhost:5173"tür.
// Eğer frontend'iniz farklı bir adreste çalışıyorsa, bu adresi güncellemeniz gerekir.
// Sunucunun kabul edeceği maksimum istek boyutunu 2 GB olarak ayarlıyoruz.
// Varsayılan limit (yaklaşık 30 MB) büyük dosyalar için yetersizdir.
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // 2 GB = 2 * 1024 * 1024 * 1024 = 2,147,483,648 bayt.
    serverOptions.Limits.MaxRequestBodySize = 2147483648;
});

// Form gönderimleri için de limiti artırıyoruz.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 2147483648;
    options.ValueLengthLimit = int.MaxValue;
});
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

// Controller'ları (yani UploadController'ımızı) servis olarak ekliyoruz.
builder.Services.AddControllers();

var app = builder.Build();

// Geliştirme ortamında daha detaylı hata sayfaları göster.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

// Eklediğimiz CORS politikasını uygulamaya koyuyoruz.
app.UseCors();

// "/api/upload" gibi controller rotalarını aktif hale getiriyoruz.
app.MapControllers();

// Web sunucusunu çalıştır!
app.Run();