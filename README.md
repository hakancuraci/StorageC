# StorageC

Referans kütüphaneyle aynı temel C# kullanım modelini koruyan, arka planda C
tabanlı [libsmb2](https://github.com/sahlberg/libsmb2) kullanan .NET 8 SMB2/3
istemcisidir. Ürünün, assembly'nin, namespace'in ve NuGet paketinin adı
`StorageC`'dir. Bu repo çalışır bir başlangıç uygulamasıdır; NuGet yayını öncesinde
gerçek NAS modeliniz, ağınız ve yük profilinizle entegrasyon/soak testi gerekir.

## Tasarım

- C# yalnız sabit ve küçük bir C ABI shim'i çağırır; `libsmb2` struct düzenleri
  managed ABI'ye sızmaz.
- Native client/file pointer'ları `SafeHandle` ile yönetilir. Açık stream,
  client dispose edilse bile native client referansını yaşatır.
- Her `libsmb2_context` tek özel worker thread üzerinde kullanılır. Context aynı
  anda iki thread'den çağrılmaz; async çağrılar .NET ThreadPool thread'i tutmaz.
- İç transfer buffer'ları `ArrayPool<byte>` ile yeniden kullanılır. `ReadAsync`
  ve `WriteAsync` buffer'ı yalnız native çağrı sırasında pinler.
- `libsmb2` sunucunun bildirdiği maksimum read/write boyutunu shim içinde uygular;
  kısmi write sonuçları tamamlanana kadar döngü devam eder.
- Paralel dosya transferi `PooledSmbClient` içindeki bağımsız client/context'lerle
  yapılır. Kuyruk ve client sayısı sınırlıdır.
- `libsmb2` LGPL uyumluluğu ve değiştirilebilirliği için ayrı dinamik kütüphane
  olarak paketlenir.

Hiçbir native/managed yazılım için mutlak “bellek problemi olmaz” garantisi
verilemez. Buradaki sahiplik modeli; double-free, use-after-free, açık stream
varken client'ın erken bırakılması, sınırsız kuyruk ve her chunk'ta allocation
gibi tipik riskleri tasarımla engeller. CI fixture'ı native handle sayaçlarını
stres sonunda sıfır olarak doğrular. Üretimde ASan/Valgrind (Linux), uzun soak
testi ve gerçek SMB sunucusu testi ayrıca yapılmalıdır.

## Kullanım

```csharp
using StorageC;

await using ISmbClient client = SmbClient.Create(new SmbClientOptions
{
    Server = "10.0.0.20",
    ShareName = "documents",
    Username = "app-user",
    Password = configuration["Smb:Password"]!,
    Domain = "ACME",
    ChunkSizeBytes = 1024 * 1024,
    MaxReadBytes = 64L * 1024 * 1024,
    RequireSigning = true
});

await using (var local = new FileStream("archive.zip", FileMode.Open,
    FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true))
{
    await client.WriteAsync("incoming/archive.zip", local,
        new SmbWriteOptions { Overwrite = true, ForceSync = true });
}

await using Stream remote = await client.OpenReadAsync("incoming/archive.zip");
await remote.CopyToAsync(destination);
```

Yoğun API servisinde havuzu singleton kaydedin:

```csharp
builder.Services.AddSingleton<ISmbClient>(_ => PooledSmbClient.Create(
    smbOptions,
    new SmbClientPoolOptions
    {
        MaxClients = 8,
        MaxPendingAcquires = 32,
        AcquireTimeout = TimeSpan.FromSeconds(2)
    }));
```

`OpenRead[Async]` / `OpenWrite[Async]` ile dönen stream mutlaka dispose
edilmelidir; havuz client'ı stream kapanana kadar kirada tutar. Yazma atomik
değildir: hata/iptal kısmi dosya bırakabilir. Atomik yayın için geçici dosya +
rename iş akışı ayrıca eklenmelidir.

## Build ve test

Gereksinimler: .NET 8 SDK, CMake 3.21+, C derleyicisi, Git ve Linux native
paketi için Docker.

```powershell
./build-native.ps1 -RuntimeIdentifier win-x64
dotnet build StorageC.sln -c Release
./tests/run-tests.ps1
dotnet pack src/StorageC/StorageC.csproj -c Release
```

Linux için `linux-x64` kullanın. Bu hedef Docker içinde digest ile sabitlenmiş
manylinux2014 imajında derlenir ve native dosyaların en fazla `GLIBC_2.17`
istemesi build sırasında doğrulanır. Böylece native dosyalar Debian 12 dahil
glibc 2.17+ sistemlerde yüklenebilir; çalıştırılan .NET sürümünün kendi işletim
sistemi gereksinimleri ayrıca geçerlidir. Native build, upstream `libsmb2`
commit'ini CMake `FetchContent` ile sabit revizyondan alır. Desteklenen paket
hedefleri şu anda Windows x64 ve glibc tabanlı Linux x64'tür; Alpine/musl ayrı
bir native hedef gerektirir.

## Sürekli SMB performans testleri

CI, sabit digest'li geçici bir Samba container'ına karşı gerçek SMB okuma/yazma
ve veri doğrulama senaryoları çalıştırır:

- Her push ve pull request'te 45 saniyelik report-only test çalışır.
- Her gece 01:00 UTC'de 15 dakikalık report-only soak testi çalışır.
- `v*` release tag'inde son yayımlanmış NuGet sürümü ve mevcut kod aynı runner'da
  üçer dakika ölçülür. SMB hatası, veri bozulması, en az `%25` throughput kaybı
  veya en az `%50` p95 gecikme artışı paket yayınını durdurur.

Job özetinde throughput, ops/s, p95 gecikme, hata sayısı, peak working set ve
önceki sonuca göre değişim gösterilir. Ham JSON ve Markdown raporları
`StorageC-smb-performance-*` Actions artifact'ında 90 gün tutulur.

## Üretime çıkmadan önce

1. Windows ve Linux native CI build'lerini çalıştırın.
2. Gerçek NAS ile kimlik doğrulama, signing, encryption ve izin testleri yapın.
3. Beklenen dosya boyutu/eşzamanlılıkla 12-24 saat soak testi çalıştırın.
4. Linux native build'i ASan/UBSan ve Valgrind altında çalıştırın.
5. Throughput/latency ölçümüne göre `ChunkSizeBytes` ve `MaxClients` ayarlayın;
   daha büyük değerlerin her zaman daha hızlı olmadığını varsayın.

Resmi libsmb2 API'si maksimum read/write boyutlarının sorgulanmasını ve context
temizliğinde `smb2_destroy_context` kullanılmasını tanımlar. Bu shim bu kuralları
uygular: [libsmb2 API header](https://github.com/sahlberg/libsmb2/blob/master/include/smb2/libsmb2.h).
