# Windows kurulumu

## Hazır paket

1. [Releases](https://github.com/kutsaltotem/codex-usage-monitor/releases/latest) sayfasından Windows x64 ZIP paketini indir.
2. ZIP'e sağ tıklayıp **Tümünü ayıkla** ile tamamını çıkar. EXE'yi ZIP'in içinden çalıştırma.
3. Çıkarılan klasörde **Install.cmd** dosyasını çalıştır. Uygulama `%LOCALAPPDATA%\Programs\CodexUsageMonitor` altına kopyalanır ve Başlat menüsüne **Süper Zeka Kullanımı** kısayolu eklenir. Yönetici yetkisi gerekmez.
4. Sistem tepsisi yanında kullanım kapsülleri görünür. Göstergeye tıkla veya Başlat menüsündeki kısayolu aç.

Taşınabilir kullanmak için kurulum adımını atlayıp aynı klasördeki `CodexUsageMonitor.exe` dosyasını çalıştırabilirsin. DLL'ler ve Assets klasörü EXE'nin yanında kalmalıdır. .NET çalışma zamanı pakete dahil; SDK gerekmez.

Paket dijital olarak imzalı değildir. Yalnız bu deponun Release dosyalarını indir. `SHA256SUMS.txt` içindeki ZIP özetiyle indirdiğin dosyayı karşılaştırabilirsin:

```powershell
Get-FileHash .\CodexUsageMonitor-win-x64.zip -Algorithm SHA256
```

## Sağlayıcıları bağla

- **Codex:** Windows Codex uygulamasına ChatGPT hesabınla giriş yap. Sadece geliştirici API anahtarıyla açılmış oturum abonelik kotasını vermez.
- **Gemini / Antigravity:** Antigravity'yi aç ve giriş yap; yerel kota servisi çalışır durumda olmalı. Plana göre sağlayıcının döndürdüğü limitler gösterilir.
- **Claude:** Claude Code'un abonelik OAuth oturumu gerekir. Ücretsiz Claude Desktop girişi bu sürümde kota sağlamaz. Ücretli plan/oturum yoksa marka yanındaki uyarı nedenini gösterir.

Uygulama oturum açma veya token yenileme işlemi yapmaz. Girişin süresi dolduysa ilgili sağlayıcı uygulamasında yenile, sonra paneldeki ↻ düğmesine bas.

## Günlük kullanım

Göstergeyi tıklamak paneli açar; tekrar tıklamak veya başka uygulamaya geçmek kapatır. Kota verisi2 dakikada bir güncellenir. Eski ölçüm varsa tarihi korunur. Marka yanındaki **!** üzerine gel veya tıkla: kaynak, neden ve öneri görünür.

Tepsi menüsünden **Windows oturumunda başlat**, **Düşük kota uyarıları** ve gösterge görünürlüğü seçilebilir. Geçmişin en altındaki Token kayıtları, yalnız yerel CLI toplamlarını isteğe bağlı açar. Saklama30/90/365 gün; temizleme kota geçmişini silmez.

## Güncelleme ve kaldırma

Güncelleme için yeni ZIP'i ayrı bir klasöre çıkarıp `Install.cmd` çalıştır. Yalnız kurulu uygulama dosyaları değiştirilir; `%LOCALAPPDATA%\CodexUsageMonitor` altındaki geçmiş korunur.

Kaldırmadan önce tepsi menüsünden otomatik başlangıcı kapat ve **Çıkış** seç. Ardından kurulum klasörünü ve Başlat menüsündeki kısayolu kaldır. Yerel geçmişi de kaldırmak istiyorsan uygulama veri klasörünü ayrıca sil; bu işlem geri alınamaz.

## Sorun giderme

| Belirti | Kontrol |
|---|---|
| Gösterge yok | Tepsi menüsünde görev çubuğu göstergesinin açık olması; Explorer/tepsi çalışıyor olması |
| Veri eski / yok | Markanın ! açıklaması, oturumun geçerliliği, Antigravity'nin açık olması |
| Token geçmişi boş | İlgili CLI günlükleri bulunmalı, toplama seçimi açık olmalı; Desktop sohbetleri bu sayaçlara dahil değil |
| Claude Desktop girişli ama bağlantı yok | Desktop girişi ve Claude Code abonelik oturumu farklı kaynaklardır |

Windows 11 x64'te doğrulanmış bir önizlemedir. Farklı DPI, otomatik gizlenen görev çubuğu ve Explorer yeniden başlatma sorunlarını ekran görüntüsü ve Windows sürümüyle [Issues](https://github.com/kutsaltotem/codex-usage-monitor/issues) üzerinden bildir. Oturum dosyası, API anahtarı, çerez veya özel sohbet günlüklerini yükleme.

Otomatik başlangıç tercihi Windows Görev Zamanlayıcısı ile kullanıcı oturumuna bağlıdır. Etkinleştirildiğinde küçük UsageMonitorSupervisor başlatıcısı uygulamanın kapanmasını bekler; beklenmedik/nonzero kapanmada1dk sonra yeniden açar. Tepsi menüsünden Çıkış normal0koduyla kapanır; başlatıcı da sona erer, o oturumda yeniden açmaz. Açılış kaydı10sn gecikmeli, pilde açık ve süre sınırı yoktur. Bu güncelleme henüz GitHub sürüm paketine yayımlanmamıştır.
