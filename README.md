# Windows Codex, Gemini ve Claude Kullanım Monitörü

ChatGPT/Codex, Gemini ve Claude Code kullanım durumunu Windows görev çubuğunda kompakt, ayrıntıları açılır panelde gösteren uygulama.

## Proje durumu

Depoda .NET kota toplama çekirdeği, WPF/WinForms tepsi uygulaması ve ayrı opt-in yerel token geçmişi görünümü var. Windows uygulamasını paketlemek için `scripts/publish-windows.ps1` eklendi; gerçek Windows derlemesi, gerçek uygulama oturumlarıyla uçtan uca doğrulama ve imzalı kurulum paketi henüz yapılmadı. Ekran görüntüleri tarayıcı prototipidir; sayaçlar örnektir, gerçek hesaptan alınmamıştır.

## Önizleme

`usage-monitor-mockup.png` Kullanım sekmesinin, `usage-monitor-limits.png` kota sekmesinin ekran görüntüsüdür. `usage-monitor-mockup.html` dosyasını tarayıcıda açarak sekmeler, dönem/sağlayıcı filtreleri ve grafik ayrıntılarıyla etkileşebilirsiniz. Ekrandaki bütün kullanım ve kota değerleri kurgusaldır; bu konsept herhangi bir günlük, kimlik bilgisi veya ağa erişmez. HTML dosyası logoları `assets/` klasöründen yükler; görüntüleri taşırken bu klasörü de birlikte tutun.

Görev çubuğu fikrinde her sağlayıcının logosu, sıfırlanmaya kalan süre ve kalan yüzde kapsülleri gösterilir. Açılır panelde mevcut kota çubukları “Kota limitleri” sekmesinde korunur. “Token kullanımı” sekmesinde yerel günlüklerden tarih/model sayaçları, dönem/sağlayıcı filtreleri ve token grafiği bulunur. HTML prototipindeki örnek sayaçlar ve örnek fiyat eşdeğeri gerçek ölçüm değildir; Windows uygulamasında doğrulanmış fiyat listesi olmadığı için maliyet tahmini gösterilmez.

## İçerik

- `PRODUCT-PLAN-20261005.md` — pazar örnekleri, veri kaynakları, Windows tasarımı, güvenlik kararları ve yol haritası.
- `usage-monitor-mockup.html` — örnek verilerle çalışan etkileşimli tarayıcı konsepti.
- `usage-monitor-mockup.png` ve `usage-monitor-limits.png` — Token kullanımı ve Kota limitleri sekmelerinin ekran görüntüleri.
- `assets/` — kullanıcı tarafından sağlanan sağlayıcı logo görselleri ve eşleştirme notları.

## Veri toplama çekirdeği

`windows-app/` altındaki .NET kütüphanesi Codex uygulamasının yerel oturumundan, Google Antigravity'nin Windows oturumundan ve istenirse Claude Code oturumundan kota eşitler; CLI kurmak gerekmez. Beş dakikalık yenileme ve son iyi snapshot önbelleği sağlar. Claude kota pencereleri Claude Code'un Windows'ta sakladığı OAuth oturumuyla Anthropic'in belgelenmemiş `/api/oauth/usage` uç noktasından alınır. Token yenileme veya oturum dosyalarına yazma yapılmaz. Kota snapshot geçmişi 365 gün yerel JSONL kaydıdır. Ayrı ve isteğe bağlı token geçmişi ise Codex CLI, Gemini CLI ve Claude Code günlüklerinden tarih/model/token sayaçları çıkarır; bu günlükleri kullanmıyorsan bu özelliği açman gerekmez. Saklama süresi 30, 90 veya 365 gün seçilebilir. Ham konuşma metni, dosya yolu veya oturum kimliği saklanmaz. Windows derlemesi ve gerçek hesaplarla uçtan uca doğrulama henüz yapılmadı.

Claude kota bağlantısı, kullanıcının Claude Code ile açtığı abonelik oturumunu ve toplulukça belgelenmiş, ancak Anthropic tarafından kararlı API olarak sunulmayan OAuth kullanım yolunu kullanır. Claude Code oturum JSONL biçimi de iç kullanıma yöneliktir. Bu nedenle Claude kota veya token satırları alınamazsa değer tahmini yapılmaz; kaynak/kapsam uyarısı görünür.

ChatGPT verisi Codex kullanım pencereleridir; tüm ChatGPT sohbetlerinin toplamı değildir. Gemini yalnızca sağlayıcının yanıtında bulunan kota/model kovalarını verir. Claude için 5 saatlik ve haftalık abonelik yüzdeleri gösterilir; model kapsamlı ek haftalık pencereler yanıtta geldikçe eklenir. Bu kota uç noktası Anthropic tarafından belgelenmediği için değişebilir. Claude token geçmişi yalnız Claude Code yerel oturumlarını kapsar; Claude.ai web ve masaüstü sohbetleri dahil değildir. Erişim tokenı önbelleğe/loga yazılmaz ve uygulama token yenilemez: oturum süresi dolunca ilgili CLI'da yeniden giriş gerekir. Ayrıntılı bağlantı kararı ve kısıtları [`PRODUCT-PLAN-20261005.md`](PRODUCT-PLAN-20261005.md) içindedir.

## Veri kapsamı

Kota yüzdesi/reset verisi Codex ve Antigravity uygulamalarının yerel oturumlarından gelen kota yanıtlarıyla elde edilir; token geçmişi ise ayrı CLI oturum günlüklerinden gelir. ChatGPT/Codex bağlantısı Codex kullanım pencerelerini gösterir, tüm ChatGPT sohbetlerini saymaz. Antigravity için yerel token günlükleri kapsam dışıdır. Claude token geçmişi Claude Code JSONL oturumlarına dayanır ve Claude.ai web/masaüstü sohbetlerini içermez. Token geçmişi varsayılan kapalıdır, sağlayıcı başına açılabilir, durdurulabilir ve yerel özet dosyaları silinebilir.

## Windows paketini oluşturma

Windows makinesinde .NET 10 SDK kuruluysa proje kökünde PowerShell açıp çalıştırın:

```powershell
.\scripts\publish-windows.ps1
```

Script, kendi makinenizde `artifacts\CodexUsageMonitor-win-x64-<tarih>.zip` paketini üretir. Paketi açıp `CodexUsageMonitor.exe` dosyasını çalıştırabilirsiniz. GitHub'a gönderildiğinde `.github/workflows/windows-package.yml` de Windows x64 paketini her `main` push/PR derlemesinde 14 gün süreyle indirilebilir Actions artifact'i olarak sunar. Proje paylaşıma hazır imzalı bir kurulum paketi değildir.

## Kaynaktan çalıştırma

1. Proje klasörünü Windows bilgisayarınıza alın ve .NET 10 SDK'yı kurun.
2. Windows Codex uygulamasında ChatGPT hesabınızla, Antigravity uygulamasında Google hesabınızla oturum açın. Bu kota bağlantıları için CLI kurmanız gerekmez. Claude kullanıyorsanız Claude Code oturumunu da açın.
3. `dotnet run --project windows-app/UsageMonitor.Windows/UsageMonitor.Windows.csproj` komutuyla uygulamayı başlatın.
4. Varsayılan kompakt alt şerit AppBar olarak ekranın altına yerleşir ve çalışma alanından yükseklik ayırır; tepsi menüsünden kapatılabilir.
5. Tepsi menüsündeki “Windows oturumunda başlat” seçeneğiyle otomatik açılışı isteğe bağlı etkinleştirin.
6. “Düşük kota uyarıları” varsayılan olarak açıktır; aynı kota reset penceresi için %20 ve %10 eşiğinde birer uyarı verir. Tepsi menüsünden kapatılabilir.
7. CLI günlüklerinden token geçmişi istiyorsanız Token kullanımı sekmesinde kaynak iznini açın. Codex/Antigravity uygulamalarını kullanmak kota göstergesi için yeterlidir; CLI token geçmişini açmak zorunlu değildir.
8. Ürün ve kaynak kararları için `PRODUCT-PLAN-20261005.md` dosyasını okuyun.

Bu çalışma ortamında .NET SDK/Windows bulunmadığı için derleme ve sağlayıcı hesabıyla gerçek bağlantı doğrulanmadı. `usage-monitor-mockup.html` hâlâ etkileşimli, örnek verili tasarım referansıdır.

## Varlıklar

Logo görselleri kullanıcı tarafından sağlandı. Dosya seçimi ve görsel eşleştirme notları için [`assets/README.md`](assets/README.md) dosyasına bakın.
