# Süper Zeka Kullanımı · AI Usage Bar

**Codex, Gemini ve Claude kotanı tek bakışta gör. Akışını bölmeden çalışmaya devam et.**

[Windows x64 indir](https://github.com/kutsaltotem/codex-usage-monitor/releases/latest) · [Kurulum rehberi](docs/INSTALL.md) · [English](README.en.md) · [Sorun bildir](https://github.com/kutsaltotem/codex-usage-monitor/issues)

![Windows görev çubuğu göstergesi](docs/images/taskbar.png)

Görev çubuğunun içinde, sistem tepsisinin yanında üç küçük gösterge. Bir tıkla kalan kotayı ve sıfırlanma zamanını gör; geçmişte kullanım eğilimini izle. Ayrı bir pencereyi sürekli açık tutman gerekmez.

<table><tr><td><img src="docs/images/quota.png" width="350" alt="Kota paneli" /></td><td><img src="docs/images/history.png" width="350" alt="Geçmiş ve çizgi grafiği" /></td></tr></table>

> Görseller güncel Windows uygulamasından üretilmiştir. Tüm sayaçlar **örnek veridir**; gerçek kişisel hesap veya ücretli Claude bağlantısı kanıtı değildir.

## Bir bakışta, ihtiyacın kadar bilgi

- **Görev çubuğuna yerleşir.** Şeffaf dış zemin, ortalı logolar ve okunaklı yüzde kapsülleri; ayrı görev çubuğu düğmesi oluşturmaz.
- **Tek tıkla ayrıntı.** 560 DIP yüksekliğinde panel göstergeye boşluksuz bağlanır; tekrar tıklayınca veya başka uygulamaya geçince kapanır.
- **Ne kadar güncel olduğunu söyler.** “1 dakika önce” etiketi son başarılı ölçüme dayanır. Bağlantı kesilirse son veri korunur, eski olduğu belirtilir.
- **Kota eğilimini gösterir.** Son 24 saatin saatlik ölçümleri çizgi grafiği olarak görünür. Tümü, Codex, Gemini ve Claude ayrı seçilebilir.
- **İsteğe bağlı token geçmişi.** CLI günlüklerinden günlük/model özetleri; dönem ve sağlayıcı filtreleri, üstte sabit toplam/girdi/çıktı kartları. Model dökümü gerektiğinde açılır.
- **Sade hata açıklaması.** Marka yanındaki kırmızı ünlemden neden ve çözüm önerisi okunur; genel hata kutuları ekranı doldurmaz.
- **Sürekli tarama yapmaz.** Kota 2 dakikada bir yenilenir. Paneli açmak yeni kota isteği başlatmaz; token toplama kapalıysa zamanlayıcısı çalışmaz.
- **Kontrol sende.** Başlangıçta açılma, düşük kota bildirimleri ve token saklama süresi tepsi/panel üzerinden seçilir.

## İndir, kur, bağlan

1. [Son sürümden](https://github.com/kutsaltotem/codex-usage-monitor/releases/latest) **CodexUsageMonitor-win-x64.zip** paketini indir ve tamamını bir klasöre çıkar.
2. **Install.cmd** dosyasını çalıştır. Yönetici yetkisi istemeden kullanıcı klasörüne kurar, Başlat menüsüne kısayol ekler ve uygulamayı açar. İstersen kurmadan klasördeki **CodexUsageMonitor.exe** ile taşınabilir kullan.
3. Codex veya Antigravity uygulamasında hesabına giriş yap. Gösterge sistem tepsisinin soluna yerleşir; ayrıntılar için tıkla.
4. İstersen tepsi menüsünden **Windows oturumunda başlat** seçeneğini aç. Token sayaçları istiyorsan Geçmiş bölümünün en altındaki kaynakları etkinleştir.

.NET çalışma zamanı pakete dahildir; indirilen sürümü çalıştırmak için SDK veya geliştirici API anahtarı gerekmez. Windows 11 x64 üzerinde doğrulanmıştır. Bu ilk herkese açık **önizleme sürümüdür**; ARM64, farklı DPI düzenleri, otomatik gizlenen görev çubuğu ve Explorer yeniden başlatma davranışı kapsamlı doğrulanmamıştır. Paket dijital olarak imzalı değildir; Windows uyarı gösterebilir. [Kurulum ve sorun giderme](docs/INSTALL.md).

## Hangi veriyi gösteriyor?

| Kaynak | Kota bağlantısı | Token geçmişi |
|---|---|---|
| Codex | Windows Codex uygulamasının geçerli yerel oturumu; Codex kullanım pencereleri | İsteğe bağlı Codex CLI kayıtları |
| Gemini / Antigravity | Açık ve girişli Antigravity'nin yerel kota servisi; gerektiğinde mevcut Gemini OAuth yolu | İsteğe bağlı Gemini CLI kayıtları |
| Claude | Claude Code'un yerel abonelik OAuth oturumu | İsteğe bağlı Claude Code kayıtları |

**Claude Desktop'a giriş yapmak tek başına yeterli değildir.** Ücretsiz Desktop hesabının kotası bu sürümde bağlanmaz. Ücretli Claude bağlantı yöntemi kodda bulunur; bu sürümün gerçek hesap doğrulaması Codex ve Antigravity ile yapılmıştır. Hesap oturumunu uygulama yenilemez; süre dolunca sağlayıcıda yeniden giriş gerekir.

Gösterge **kalan yüzdeyi**, kota geçmişi ise **kullanılan 5 saatlik yüzdeyi** (bu pencere yoksa haftalık yüzdeyi) gösterir. “Son 24 saat”, ölçümlerin zaman aralığıdır; o gün tüketilen toplam kota değildir. Antigravity'nin ayrı Claude/GPT grubu, Claude Code aboneliğiyle karıştırılmaz; Gemini çizgisinin dışında tutulur. Token sayısı abonelik yüzdesi veya fatura tutarı değildir. Web/Desktop sohbetlerinin tamamı token geçmişine dahil değildir.

## Yerel veriler ve kaynak kullanımı

Kota ve token özetleri `%LOCALAPPDATA%\CodexUsageMonitor` altında tutulur. Token toplama varsayılan kapalıdır; 30/90/365 gün seçilebilir. Saklanan özetler konuşma metni içermez. Uygulama kişisel istatistikleri bir izleme sunucusuna yüklemez; kota okumak için sağlayıcının servisine mevcut oturumuyla istek gönderir. Oturum anahtarları uygulamanın geçmiş dosyalarına yazılmaz.

WPF tabanlı uygulama sıfır bellek maliyetine sahip değildir. Yerel kısa ölçümlerde yaklaşık 160–195 MiB çalışma kümesi görüldü; bunlar kontrollü benchmark değildir ve bilgisayara göre değişir. Panel gerektiğinde oluşturulur, gizlenince görsel öğeleri bırakılır. Yazılım çizimi bellek/CPU arasında bir tercihtir.

## Geliştirme

Windows ve .NET 10 SDK ile:

```powershell
dotnet run --project windows-app/UsageMonitor.Windows/UsageMonitor.Windows.csproj
.\scripts\publish-windows.ps1
```

Paket `artifacts/` altında üretilir. Doğrulama ayrıntıları [Windows test raporunda](WINDOWS-VERIFICATION-20261006.md); görselleri yeniden üretme komutu [görsel notlarında](docs/images/README.md). HTML dosyaları eski, örnek verili tasarım prototipleridir; güncel ürünün yerine geçmez.

Bağımsız bir projedir; OpenAI, Google veya Anthropic tarafından üretilmez ya da onaylanmaz. Marka ve logolar ilgili sahiplerine aittir. Kota yolları sağlayıcıların kararlı herkese açık API sözleşmeleri değildir; değişebilir veya erişim reddedilebilir.

Otomatik başlangıç tercihi Windows Görev Zamanlayıcısı ile kullanıcı oturumuna bağlıdır. Etkinleştirildiğinde küçük UsageMonitorSupervisor başlatıcısı uygulamanın kapanmasını bekler; beklenmedik/nonzero kapanmada1dk sonra yeniden açar. Tepsi menüsünden Çıkış normal0koduyla kapanır; başlatıcı da sona erer, o oturumda yeniden açmaz. Açılış kaydı10sn gecikmeli, pilde açık ve süre sınırı yoktur. Bu güncelleme henüz GitHub sürüm paketine yayımlanmamıştır.
