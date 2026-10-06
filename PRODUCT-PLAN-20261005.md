# Windows Codex, Gemini ve Claude Kullanım Monitörü — Ürün Planı

> **Güncel durum — 6 Ekim 2026:** Windows11x64 paketi derlendi ve yerelde kuruldu. Gösterge artık görev çubuğunun içinde; panel350x560DIP, kota taraması2dk ve panel açılışı yeni istek yapmaz. Antigravity'nin çalışan yerel servisi önceliklidir. Geçmiş çizgi grafiği/sağlayıcı filtreleri, sabit token toplamları ve lazy model dökümü uygulanmıştır. Codex ve Antigravity canlı; ücretli Claude gerçek hesap doğrulaması bekliyor. Aşağıdaki ilk plan tarihsel taslaktır; ürünün güncel kurulumu ve sınırları [README](README.md) ve [kurulum rehberinde](docs/INSTALL.md) geçerlidir.


İlk araştırma: 5 Ekim 2026 · KDE Agents Usage ve Windows Usage Monitor incelemesi: 6 Ekim 2026

## Güncel karar — 6 Ekim 2026

Bu karar, aşağıdaki ilk taslaktaki “kimlik bilgilerini okumama / belgelenmiş resmî kaynak bekleme” önerisinin yerine geçer. Kota bağlantısı kullanıcının zaten giriş yaptığı Windows Codex ve Google Antigravity uygulamalarının yerel oturumlarından yapılır; bu amaçla CLI kurulması veya geliştirici API anahtarı gerekmez. İsteğe bağlı token geçmişi ayrı CLI günlüklerine dayanır.

- V1 sağlayıcıları ChatGPT Plus/Pro hesabının Codex kullanım pencereleri, Gemini ücretli hesabının sağlayıcının bildirdiği kota pencereleri ve Claude Pro/Max/Team/Enterprise abonelik hesabının kullanım pencereleridir.
- ChatGPT/Codex kotası, Windows Codex uygulamasının kullandığı yerel Codex oturumundan (`%USERPROFILE%\.codex\auth.json`, varsayılan dosya saklama modu) `chatgpt.com/backend-api/wham/usage` üzerinden okunur. Bu veri Codex kullanım pencereleridir; genel ChatGPT sohbetlerinin toplamı değildir.
- Gemini kotası, Antigravity uygulamasının Windows Credential Manager'da sakladığı oturumdan (`gemini:antigravity`) Cloud Code quota yanıtlarından okunur. Yanıtta bulunan model, yüzde ve reset pencereleri gösterilir; beş saatlik/haftalık değerler yoksa üretilmez.
- Claude kotası, Windows'ta `%USERPROFILE%\\.claude\\.credentials.json` dosyasındaki Claude Code `claude.ai` OAuth erişim anahtarıyla Anthropic'in `api.anthropic.com/api/oauth/usage` uç noktasından okunur. Bu uç nokta herkese açık, kararlı API olarak belgelenmemiştir; hata veya şema değişikliğinde son iyi snapshot korunur. Uygulama kimlik bilgilerini yenilemez veya değiştirmez.
- Kota eşitlemesi uygulama açılışında, panel açılınca ve varsayılan olarak beş dakikada bir yapılır. Başarılı okumalar zaman damgasıyla yerel geçmişe eklenir; ağ veya oturum hatasında son iyi değer eski olarak görünür.
- Detaylı “Token kullanımı” görünümünde yerel kota snapshot geçmişi ile CLI günlüklerinden çıkarılan token sayaçları ayrı serilerdir. Codex geçmişi yerel Codex oturum kayıtlarından; Gemini token geçmişi Gemini CLI JSONL günlüklerinden; Claude token geçmişi Claude Code JSONL oturum kayıtlarından toplanır. Claude Code günlük biçimi iç kullanıma yöneliktir ve sürümler arasında değişebilir. Gemini/Antigravity kotası ayrı bir kota yoludur; Antigravity token günlükleri kapsam dışıdır. Claude.ai web ve masaüstü sohbetleri token geçmişine dahil değildir.
- Erişim tokenları uygulama deposuna veya uzaktaki sunucuya gönderilmez. Mevcut yerel uygulama oturumları salt okunur kullanılır; tokenlar günlüklere, snapshot geçmişine veya ekran çıktısına yazılmaz. Uygulama token yenilemez veya oturum dosyalarını değiştirmez; oturum sona ererse ilgili uygulamada tekrar giriş istenir.
- Bu kota yolları sağlayıcıların herkese açık kararlı abonelik API sözleşmeleri değildir; alan adları/yanıt şemaları değişebilir veya istekler reddedilebilir. Her kart kaynak yöntemini, son başarılı eşitleme saatini ve eski/veri yok durumunu gösterir; bozuk yanıttan sayaç tahmin edilmez.

Windows uygulaması KDE plasmoidini çalıştırmayacak. KDE örneğinden periyodik toplama, önbellek, eski veri işareti ve Codex/Claude Code yerel token geçmişi yaklaşımı; Windows Usage Monitor örneğinden Gemini oturumu/kota eşleme bilgisi alınır. Tepsi/panel ve görev çubuğu yüzeyi mevcut Windows tasarımına göre uygulanır.

## Uygulama durumu — 6 Ekim 2026

`windows-app/` altında C# veri çekirdeği ve `UsageMonitor.Windows/` altında WPF/WinForms kabuğu eklendi: Codex/Gemini/Claude yerel oturum okuyucuları, kota yanıtı adaptörleri, istek sınırları, hata durumları, beş dakikalık eşitleyici, son iyi snapshot önbelleği, 365 günlük kota geçmişi, tepsi menüsü, kullanıcı kayıt defterinden açılışta başlat seçeneği, yatay ilerleme çubuklu kota sekmesi ve isteğe bağlı alt AppBar şeridi. Şerit her sağlayıcı için varsa 5 saatlik ve haftalık kotayı logo, kalan süre ve yüzde kapsülleriyle gösterir. Token geçmişi için 30/90/365 günlük saklama seçeneği de eklendi. Windows derlemesi ve gerçek oturum doğrulaması bekliyor; Windows paket betiği ve Actions akışı eklendi. HTML önizlemeleri örnek veridir.

Codex, Gemini CLI ve Claude Code token günlükleri için kota polling'inden ayrı, yerel toplulaştırma okuyucuları eklendi. Token geçmişi sağlayıcı başına varsayılan kapalıdır; seçilen günlükleri tarama, otomatik eşitleme, durdurma, 30/90/365 gün saklama ve özeti silme kontrolleri token sekmesindedir. Sekmede dönem/sağlayıcı filtreleri, toplam/girdi/çıktı özeti, zaman grafiği ve model dökümü bulunur. Ham konuşma kaydı saklanmaz. Windows derlemesi, gerçek CLI dosyalarıyla doğrulama ve AppBar davranışı henüz sınanmadı.

V1 bağlayıcıları token yenileme yapmaz ve CLI kimlik bilgisi dosyasını değiştirmez. Erişim tokenı reddedilirse eski snapshot tutulur ve yeniden giriş istenir. Bu nedenle arka plan eşitlemesi geçerli yerel CLI oturumu sürdüğü sürece otomatik işler; hesap oturumunu sessizce yenileyeceği varsayılmaz.

## 1. Ürün kararı

Windows'ta sistem tepsisinde yaşayan, isteğe bağlı olarak görev çubuğunun hemen üst kenarına sabitlenen, hafif bir kullanım monitörü yapalım. Her sağlayıcının verisini kendi anlamıyla gösterelim; birbirinden farklı kotaları tek bir toplam yüzdeye çevirmeyelim.

Piyasada bu fikrin doğrudan benzerleri var. Ürünün farkı, küçük ve okunaklı Windows görünümünü sağlayıcıların gerçek kota yanıtlarıyla birleştirmek; her sayının kaynağını ve son eşitleme zamanını göstermek; kota ölçümünü yerel token geçmişinden ayırmaktır. Güncel bağlantı kararı yukarıdaki “Güncel karar” bölümünde kayıtlıdır.

## 2. Kullanıcı ve temel iş

Hedef kullanıcı, Windows bilgisayarında Codex, Gemini ve Claude Code aboneliği kullanan kişi. Kullanıcı bir terminal veya web paneli açmadan şunları anlayabilmeli:

- Hangi sağlayıcıda kota yanıtı başarıyla eşitlendi?
- Varsa hangi kullanım penceresi dolmak üzere, ne zaman sıfırlanıyor?
- Sayı ne zaman alındı ve canlı mı, önbellekten mi geliyor?
- Veri henüz alınamadıysa son ölçüm ne zaman yapıldı ve tekrar oturum açmak gerekiyor mu?

Bir bakışta okunması gereken bilgi yüzde, pencere, sıfırlanma zamanı ve veri yaşıdır. İstemler, yanıtlar, proje adları ve kaynak kodu bu ürünün kullanım alanı değildir.

## 3. Pazar araştırması

İncelenen örnekler Windows tepsi/görev çubuğu uygulamaları, macOS menü çubukları ve terminal araçlarıdır. En yakın doğrudan rakipler ilk üç satırdır.

| Ürün | Platform ve kapsam | Plan için alınacak ders |
| --- | --- | --- |
| [Usage Monitor](https://github.com/sb-git-cs/usage-monitor) | Windows; Codex ve Gemini dahil CLI oturumlarını okur, kullanım pencerelerini servis yanıtlarından eşler; tepsi, panel ve görev çubuğu şeridi sunar. | Windows Credential Manager, CLI tokenları, quota parsing, yenileme ve stale cache için Windows'a yakın teknik örnektir. Kullanılan endpointlerin sürüm kararlılığı ayrıca izlenmeli. |
| [AI Quota Deck](https://github.com/JoshuaWang2211/ai-quota-deck) | Windows; panel, yüzen widget ve dar şerit görünümleri; önbellek ve başlangıç seçeneği var. Bazı sağlayıcılar için tarayıcı köprüsü istiyor. | Kullanıcıların hem ayrıntılı panel hem kısa görünür şerit istediğini gösteriyor. Tarayıcı oturumu/eklentisi bizim varsayılan bağlantı yöntemimiz olmamalı. |
| [Codex Usage](https://github.com/upstream-ray/codex-usage-monitor) | Yerel Windows uygulaması; Codex, isteğe bağlı Claude Code ve Antigravity; 5 saat/hafta pencereleri, sayaçlar, uyarılar ve çoklu monitör desteği. | Hafif, yerel kurulum ve ayrı ayrı açılıp kapanan sağlayıcılar temel beklenti hâline gelmiş. Bir sağlayıcının arızası diğerlerini durdurmamalı. |
| [ClaudeTray](https://github.com/alegauss/claude-tray) | Windows tepsi uygulaması; Claude Code, birden fazla profil, yerel geçmiş, harcama temposu tahmini ve tanılama. | Çoklu hesap sahipliği, kaynağı belli tanılama ve token/metrik ayrımını iyi ele alıyor. Tahminler canlı kota gibi gösterilmemeli. |
| [Claude Usage Tray](https://github.com/ksmaster03/claude-usage-tray) | Claude'a odaklanan Windows tepsi uygulaması; beş saatlik ve haftalık sayaçlar. | Sade, tek sağlayıcı deneyimi faydalı; bizim çoklu sağlayıcı panelini gereksiz yoğunlaştırmamamız gerekir. |
| [Claude Meter](https://github.com/JackBhanded/claude-meter) | Windows görev çubuğunda Claude kullanım göstergesi ve sıfırlanma sayacı. | Dar görev çubuğu yüzeyinde model/pencere seçimini ve okunabilirliği test etmeliyiz. |
| [CodexBar](https://github.com/steipete/CodexBar) | macOS menü çubuğu; çok sayıda kodlama sağlayıcısı, sıfırlanma pencereleri, özelleştirilebilir gösterim ve CLI. | Sağlayıcı sayısı büyüdükçe ortak ölçüm sözleşmesi ve sağlayıcı başına düzenleme ihtiyacı artıyor. Başlangıçta üç sağlayıcıya odaklanalım. |
| [AIUsageBar](https://www.aiusagebar.com/) ve [Code Meter](https://codemeter.dev/) | macOS; geniş sağlayıcı listeleri, geçmiş, uyarılar ve maliyet/kota görünümleri sunan ürünler. | Mac menü çubuğu fikri kategori standardı; Windows'ta kalite için native tepsi davranışı ve düzgün kurulum gerekir. |
| [AI Usage Widget](https://github.com/odrasile/ai-usage-widget) | Tauri tabanlı masaüstü widget; Codex, Claude Code ve Gemini yerel kullanımını tek ekranda toplamayı hedefliyor. | Çapraz platform yapı mümkün, ancak Windows'a özel tepsi, DPI, görev çubuğu ve çoklu monitör işleri yine ayrıca çözülmeli. |
| [KDE Agents Usage](https://github.com/yusufipk/kde-agents-usage) | KDE Plasma 6; kota pencerelerini CLI OAuth dosyalarından okur; Codex ve Claude Code yerel kayıtlarından dönem, gün ve model bazında token dökümü üretir. Beş dakikada bir yeniler, hata halinde son başarılı değeri tutar. | Kota polling/caching ile oturum token geçmişini ayrı veri hatlarında tutalım; ayrıntı sekmesinin filtre ve model grafiği yapısını alalım. Windows'a KDE arayüzünü değil, veri hattını uyarlayalım. |
| [coding_agent_usage_tracker](https://github.com/Dicklesworthstone/coding_agent_usage_tracker) | Terminal aracı; Codex, Claude, Gemini ve başka sağlayıcılar için kullanım/limit görünümü. | CLI çıktısı tanılama için iyi bir ek olabilir; son kullanıcı arayüzünün yerine geçmez. |
| [SessionWatcher for Windows](https://sessionwatcher.com/windows) | Windows uygulaması; çok sayıda kodlama aracını görev çubuğu yüzeyinde izlemeyi amaçlıyor. | Ürün sitesi olan örneklerde de çok sağlayıcıya yayılma var. İlk sürümde kapsamı büyütmek yerine güvenilir üç sağlayıcı yuvası sunalım. |

### Pazarın ortak kalıpları

- Tepsi simgesi ve sağ tık menüsü, uygulamayı arka planda bulmayı sağlar.
- Kısa şerit veya görev çubuğuna yakın widget, yüzdeyi ve sıfırlanma süresini gösterir.
- Açılır panel sağlayıcı başına ayrı 5 saatlik/haftalık pencereleri, hesapları ve hata durumlarını gösterir.
- Daha ayrıntılı ürünlerde kota görünümünden ayrı yerel kullanım geçmişi bulunur: tarih aralığı ve sağlayıcı filtreleri, günlere göre grafik, model dökümü ve API liste fiyatlarıyla yaklaşık eşdeğer maliyet.
- Oturum açılışında başlama, el ile yenileme ve düşük kota uyarıları sık rastlanan beklentilerdir.
- Bazı projeler mevcut CLI kimlik bilgilerini okuyup kota servislerine istek atıyor. Kullanıcı bu veri toplama şeklini kabul etti; V1 bunu geliştirici API anahtarı almadan, yalnız yerel masaüstü uygulamasında uygulayacak. Kaynak ve hata durumu her zaman görünür olacak.

### KDE Agents Usage'tan aldığımız kararlar

- İkinci bir **Kullanım** sekmesi ekle: tarih aralığı ve sağlayıcı filtresi, yığılmış günlük/haftalık/aylık grafik, en çok kullanılandan başlayarak yatay model dökümü ve ayrıntıda input/output/cache sayaçları.
- Kota sekmesini ayrı ve değişmeden tut. İlk tarama, son güncelleme, eski önbellek, yenileme hatası, boş dönem ve fiyatı bilinmeyen modeller için açık durumlar göster.
- API fiyatı eşdeğerini ekle, ama tutarı abonelik kullanımı, fatura veya gerçek harcama diye adlandırma. Model fiyatı bilinmiyorsa bu tokenları ayrıca say.
- Kota toplayıcısının yerel CLI oturumu + kota okuma mantığını alıyoruz. KDE örneğinden periyodik eşitleme/önbellek ve Codex token geçmişini; Windows Usage Monitor örneğinden Windows Gemini oturumu/kota eşlemesini alıyoruz. Gemini token geçmişi ise Antigravity'den değil, resmî Gemini CLI oturum kayıtlarından okunur.
- Yerel CLI günlükleri kaynak olabilir; ancak kullanıcı başına açık izin, kapsam açıklaması, ayarlanabilir saklama ve geçmişi silme gerekir. Yalnız token sayaçlarını yerel olarak çıkarırız; prompt/yanıt, dosya/proje yolu, oturum kimliği veya ham kayıtları saklamayız.
- Referans depo MIT lisanslı. Tasarım ve ürün davranışını yeniden uyguluyoruz; ileride kaynak kodu kopyalanırsa MIT bildirimi ve lisans koşulları korunmalı.

## 4. Sağlayıcı başına veri gerçekliği

| Hesap | V1 veri kaynağı | Ne gösteriyoruz |
| --- | --- | --- |
| ChatGPT Plus/Pro — Codex kullanımı | Yerel Codex CLI OAuth oturumundan `chatgpt.com/backend-api/wham/usage` yanıtı | Codex rate-limit pencereleri, plan etiketi ve reset zamanları. Bu genel ChatGPT web sohbeti toplamı olarak etiketlenmez. |
| Gemini ücretli hesabı / Google AI Pro | Yerel Gemini CLI/Antigravity oturumuyla Cloud Code quota yanıtları (`loadCodeAssist`, `retrieveUserQuotaSummary`, `retrieveUserQuota`, `fetchAvailableModels`) | Yanıtta gerçekten bulunan Gemini model bucketları, kalan oranlar ve reset zamanları. 5 saat/hafta alanı gelmezse üretilmez. |
| Claude Pro/Max/Team/Enterprise — Claude Code | Yerel Claude Code `claude.ai` OAuth oturumundan `api.anthropic.com/api/oauth/usage` yanıtı | 5 saatlik, haftalık ve yanıtta varsa model kapsamlı haftalık yüzdeler/resetler. OAuth kullanım ucu belgelenmemiş; değişebilir ve kararlı API garantisi yoktur. |
| API key veya Vertex kullanımı | Bu V1'de bağlanmaz; geliştirici API key istenmez. | API proje kullanımı abonelik kotası gibi gösterilmez. |

Bu endpointler uygulamanın kabul edilmiş veri kaynaklarıdır; sağlayıcıların kararlı, herkese açık abonelik API sözleşmeleri oldukları iddia edilmez. Şema değişikliği, 401/403 veya 429 durumunda son başarılı veri eski işaretlenir. Yüzde veya reset tahmin edilmez.

**Önemli ölçüm kuralı:** Abonelik kotası, API maliyeti, yerel oturum tokenları ve fatura raporu birbirinin yerine kullanılamaz. Gerekli alan yoksa yüzde çizilmez; boş yanıt `%0` sayılmaz.

## 5. V1 kapsamı

### V1'de olacak

- Windows 11 x64 öncelikli, Windows 10 22H2 uyumluluğu hedeflenen yerel uygulama.
- Tek örnek çalışan süreç, bildirim alanı simgesi, sağ tık menüsü, açılır mini panel ve isteğe bağlı görev çubuğu kenar şeridi.
- V1 sağlayıcıları ChatGPT Plus/Pro hesabının Codex pencereleri, Gemini/Google AI Pro kota yanıtları ve Claude Pro/Max/Team/Enterprise kullanım pencereleridir.
- Açılır panelde mevcut yatay ilerleme çubuklarının görseli korunur; Limitler ve Kullanım sekmeleri olur.
- Kota senkronu uygulama açılışında, panel açılınca ve her beş dakikada bir çalışır. Her başarılı ölçüm tarihli yerel snapshot olarak saklanır; hata olduğunda son iyi snapshot yaşıyla görünür.
- Token kullanımı sekmesi seçilen dönem için yerel CLI token geçmişini grafikle gösterir. Kota snapshot geçmişi ayrı JSONL serisinde biriktirilir; kota geçmişi grafiği henüz arayüze bağlanmamıştır.
- Token geçmişi Codex, Gemini CLI ve Claude Code için sağlayıcı başına açılıp kapatılabilir. Ham prompt/yanıt, kod, dosya/proje yolu veya oturum kimliği kalıcı saklamaya girmez. Claude.ai web ve masaüstü sohbetleri kapsam dışıdır.
- Gün/model token dökümü desteklenen log alanlarına göre input/output/cache bileşenlerini gösterir; olmayan bileşen “veri yok” olarak görünür.
- API liste fiyatı eşdeğeri için doğrulanmış bir yerel katalog henüz yoktur; bu nedenle Windows arayüzünde maliyet üretilmez. Mockup'taki temsili maliyet yalnız tasarım örneğidir.
- Şerit üç sağlayıcı logosunu (ChatGPT/OpenAI, Gemini ve Claude), varsa 5 saatlik/haftalık pencere sürelerini ve kalan yüzde kapsüllerini içerir.
- Sağlayıcı başına bağlantı, son kontrol, kaynak yöntemi, yeniden giriş yönergesi, eski veri ve kısmi yanıt durumları gösterilir.
- Şimdi yenile, uygulamayı gizle/çıkış ve üç kaynak için isteğe bağlı otomatik token geçmişi toplama.
- Kullanıcı geçmişi silebilir; kullanım kayıtları buluta yüklenmez.

### V1 dışında

- Grok veya başka sağlayıcı hesapları.
- ChatGPT web sohbetlerinin tamamını sayan bir sayaç; V1 yalnız Codex kullanım pencerelerini gösterir.
- API key, API fatura/harcama veya Vertex proje metrikleri.
- Tarayıcı profili, web cookie'si, prompt/yanıt, proje dosyası veya terminal çıktısı okuma.
- OAuth tokenlarını buluta gönderme, hesap ayarlarını değiştirme veya kullanıcıdan tokenı elle yapıştırmasını isteme.
- Yanıtta bulunmayan quota limitini, 5 saat/hafta penceresini veya token miktarını tahmin etme.

## 6. Deneyim ve arayüz

### Ekranlar

1. **Tepsi simgesi:** uygulamanın çalıştığını gösterir; tooltip'te üç sağlayıcının canlılık/yenileme durumu bulunur.
2. **Kısa gösterge şeridi:** ChatGPT/OpenAI, Gemini ve Claude logoları, varsa pencerelerin kalan zamanı ve kalan yüzdesi. Zaman ve yüzde ayrı kapsüllerdedir; `[]` kullanılmaz. Yanıtta bulunmayan değer `—` olur.
3. **Açılır panel / Kota limitleri:** sağlayıcı başına kaynakta bulunan pencereler, mevcut yatay bar görünümü, kalan yüzde, reset zamanı, kaynak ve son başarılı eşitleme. Pencereler yanıt yoksa çizilmez.
4. **Açılır panel / Token kullanımı:** isteğe bağlı yerel CLI token geçmişi için dönem/sağlayıcı filtreleri, token grafiği ve model dökümü. Fiyat tahmini henüz yoktur. Kota snapshot grafiği ayrı bir sonraki adımdır.
5. **Ayarlar:** sağlayıcı seçimi, şerit görünürlüğü/konumu, Windows başlangıcı, yenileme, token geçmişi izni ve saklama süresi, dil ve geçmişi silme.
6. **Tanılama:** oturum yok, çevrimdışı, 401/403, 429, zaman aşımı, kısmi yanıt ve desteklenmeyen şema durumlarını gösterir. Token veya ham yanıt yazılmaz.

### Ayrıntılı kullanım verisi

- Kota snapshotları her başarılı beş dakikalık eşitlemede eklenir; son 365 gün varsayılan olarak tutulur. Snapshot grafiği henüz arayüze eklenmemiştir.
- Kullanıcı Codex, Gemini CLI ve Claude Code token geçmişini kaynak başına açabilir. Günlük dosyaları artımlı taranır; günlük/model/token sayaçlarına ek olarak düzeltmeleri eşlemek için hashli anahtarlar ve cursor konumları yerelde saklanır. Ham prompt/yanıt kaydedilmez.
- Dönem seçenekleri 7, 30, 90 gün ve 1 yıldır. Sağlayıcı filtresi tümü, ChatGPT/Codex, Gemini veya Claude'dur.
- Token günlüğü model, input, output, cache read ve cache write alanlarını sağlıyorsa bunlar ayrı gösterilir. Eksik bileşen sıfır kabul edilmez.
- API fiyat eşdeğeri hesaplanmıyor; gerçek token toplamı abonelik maliyeti veya fatura gibi sunulmuyor.
- Büyük dosyalar artımlı ve sınırlı bellekle taranır. Ham prompt/yanıt, kod, dosya yolu, proje adı veya oturum kimliği saklanmaz. Kullanıcı toplama iznini durdurabilir, 30/90/365 günlük saklama süresini seçebilir ve yerel özeti silebilir.
- Grafik, fare üzerine gelme ve klavye odağında örnek zamanını ve sağlayıcı/model kırılımını verir; aynı metrikler erişilebilir tablo olarak sunulur.

### Okunabilirlik ve yerleşim hedefleri

- **Kısa şerit:** üç özgün logo: ChatGPT/OpenAI, Gemini ve Claude; hedef genişlik 420–620 DIP, yükseklik 36–40 DIP. Her sağlayıcı için yanıtın sağladığı 5 saatlik/haftalık pencereler gösterilir. Reset süresi ve kalan yüzde ayrı kapsüller olur; arada ince ayraç bulunur. Tooltip pencere adını tam yazar. Stale olduğunda veri yaşı görünür.
- **Açılır panel:** 400–480 DIP genişlik, üç sağlayıcı kartı için 420–620 DIP yükseklik. Başlıkta son eşitleme yaşı ve Yenile eylemi bulunur. Her kartta logo/ad, varsa plan etiketi, kota pencereleri, kaynak, durum ve tekrar giriş yönergesi yer alır.
- Marka logoları kullanıcı tarafından sağlanan özgün dosyalardan yerelde paketlenir; uygulama açılışında marka sitelerinden indirilmez.
- Kaynakta olmayan veri `—` veya açıklamalı boş durum olarak gösterilir; sahte ilerleme barı oluşturulmaz.
- Ana metrik 15–17 px, ikincil metin en az 12–14 px; tıklanabilir hedefler en az 32 px. Renk ikon ve metinle desteklenir. Açık/koyu tema, klavye odağı ve Windows yüksek kontrastı ele alınır.
- Sağlayıcı limitleri tek yüzde veya birleşik doluluk çubuğunda toplanmaz.

### Durum adları

- Canlı: son yanıt belirtilen yenileme aralığı içinde.
- Önbellek: son başarılı snapshot gösteriliyor; ekranda kaç dakika eski olduğu görünür.
- Bağlı değil: resmî CLI/hesap bağlantısı bulunamadı.
- Desteklenmiyor: sağlayıcının bu hesap türü için otomatik kota arayüzü yok.
- Yeniden denenecek: geçici ağ veya sağlayıcı hatası; sonraki deneme zamanı gösterilir.
- Kısmi: yüzde veya reset gibi alanlardan biri sağlayıcı yanıtında yok.

Renk tek başına anlam taşımamalı. Renkle birlikte yüzde, etiket, simge ve ekran okuyucu metni verilir. Eşik uyarısı varsayılan olarak %20 kalan ve %10 kalan seviyelerinde, sessiz Windows bildirimiyle çıkar; her reset penceresinde aynı eşiği bir kez bildirir. Önbellek veya desteklenmeyen kart uyarı üretmez.

## 7. Windows'ta görev çubuğu davranışı

Windows uygulama aracı çubuğu (AppBar), görev çubuğuna benzeyen ayrı bir penceredir; görev çubuğunun içine eklenti yerleştirme mekanizması değildir. Microsoft'un SHAppBarMessage arayüzü kenara sabitlemeyi, görev çubuğuyla çakışmayı yönetmeyi ve görev çubuğu yer değiştirince uygulama çubuğunu yeniden konumlandırmayı sağlar. Normal AppBar, masaüstünün kullanılabilir alanını daraltır.

Bu nedenle V1'de iki yüzey sunalım:

- Güvenilir temel: bildirim alanı simgesi + açılır panel.
- Kullanıcının isteğiyle: kompakt alt AppBar ilk açılışta etkin olur ve tepsi menüsünden kapatılabilir. AppBar masaüstü çalışma alanından yükseklik ayırır; ilk çalıştırmada bu etki açıkça belirtilir. DPI, çoklu ekran, görev çubuğu otomatik gizleme ve tam ekran davranışı Windows doğrulamasında ele alınır.

Şerit gerçekten Windows görev çubuğunun içine enjekte edilmemeli. Explorer belleğine müdahale, shell hook, görev çubuğu patch'i veya üçüncü taraf shell modifikasyonu kullanmayalım. Böylece Explorer yeniden başlatma, farklı Windows sürümleri ve güvenlik yazılımı sorunlarını azaltırız. Çoklu monitörde ilk sürüm ana ekrana odaklanır; diğer ekrana taşıma sonraki kabul çalışmasına kalır.

## 8. Teknik mimari

### Önerilen yığın

- C# ve güncel .NET LTS sürümü.
- WPF ile küçük, native ayar/panel arayüzü; Windows Forms NotifyIcon bileşeni ile tepsi simgesi.
- Sadece görev çubuğu/AppBar konumu için dar Win32 P/Invoke katmanı.
- WebView veya gömülü tarayıcı yok.
- Per-user self-contained x64 dağıtım; kullanıcı izinleriyle kurulum. Taşınabilir ZIP ve imzalı kurulum paketi seçenekleri.

WPF önerisinin nedeni Windows'a özgü tepsi ve shell entegrasyonunu küçük bir pakette kontrol edebilmesi. Tauri/Electron masaüstü örnekleri var, fakat bu ürünün MVP ihtiyacı bir Windows arayüzü ve yerel süreç yönetimi; web katmanı eklemek ölçüm doğruluğunu iyileştirmiyor.

### Bileşenler

1. **UI:** TrayContext, flyout, kompakt AppBar, ayarlar ve tanılama.
2. **UsageCoordinator:** tek zamanlayıcı, sağlayıcı başına yenileme kuyruğu, önbellek ve hata geri çekilmesi.
3. **ProviderConnector:** her sağlayıcı için ayrı keşif, okuma, açılacak resmî sayfa ve destek durumu.
4. **UsageDomain:** sağlayıcıdan bağımsız kota snapshotı ve pencere modeli. Alanlar: sağlayıcı, varsa hesap/plan etiketi, veri türü, değer, payda varsa payda, pencere kimliği/süresi, model kapsamı varsa model etiketi, reset UTC, okuma UTC, kaynak yöntemi, canlı/önbellek durumu ve destek seviyesi. Model kapsamlı haftalık pencereler yalnızca connector bunları açıkça sağlarsa çizilir.
5. **LocalUsageReader:** kullanıcı izni verilen CLI kayıtlarını yerelde salt okunur tarar; yalnız doğrulanmış sayaç alanlarını alır ve ham kayıt içeriğini kalıcılaştırmaz. Biçimi doğrulanmayan sağlayıcıya erişmez.
6. **PriceCatalog:** API liste fiyatı kataloğunu kaynağı/tarihiyle önbelleğe alır; bilinmeyen model fiyatını “bilinmiyor” tutar.
7. **LocalStore:** uygulama ayarları ve kota snapshotları için SQLite. Yerel kullanım özeti ayrı tabloda ve açık izinle saklanır; yalnız tarih/sağlayıcı/model/token alanları ve fiyat tarihi vardır. Kullanıcı geçmişi silebilir ve saklama süresini ayarlayabilir.
8. **NotificationPolicy:** eşik, tekrar engelleme ve sessiz saatler.
9. **Diagnostics:** yalnız uygulama sürümü, CLI sürümü, sağlayıcı adı, zaman aşımı/hata türü ve tekrar zamanını içeren sansürlü günlük.

### ChatGPT/Codex bağlantı akışı

1. Windows Codex uygulamasının kullandığı yerel oturumu denetle; varsayılan dosya `%USERPROFILE%\.codex\auth.json`, `CODEX_HOME` ayarlıysa o dizin kullanılır. CLI kurmak gerekmez. Codex farklı bir Windows credential store ayarıyla kullanılıyorsa dosya okuyucusu o biçimi henüz kapsamayabilir.
2. Yalnız abonelik OAuth oturumu (`tokens.access_token`) kabul edilir. API key ile oturum açılmışsa abonelik kotası desteklenmeyen durum olarak gösterilir.
3. Token ve varsa `account_id` ile `https://chatgpt.com/backend-api/wham/usage` adresinden JSON kullanım yanıtı alınır. Süresi kısa pencere yaklaşık 5 saat, uzun pencere haftalık olarak sınıflandırılır; yanıtın reset alanı korunur.
4. Token 401/403 verirse mevcut snapshot eski işaretlenir ve kullanıcıya Codex uygulamasında tekrar giriş yapması söylenir. V1'de refresh token uygulama tarafından değiştirilmez.
5. Token geçmişi gerekiyorsa `CODEX_HOME\sessions` ve `archived_sessions` JSONL kayıtlarından yalnız token sayaçları artımlı okunur; ham konuşma metni saklanmaz.

### Gemini bağlantı akışı

1. Önce Antigravity uygulamasının Windows Credential Manager'daki `gemini:antigravity` oturumu aranır; CLI kurmak gerekmez. İsteğe bağlı Gemini CLI oturum dosyaları `%USERPROFILE%\.gemini\oauth_creds.json` ve `%USERPROFILE%\.gemini\antigravity-cli\antigravity-oauth-token` da okunabilir.
2. Mevcut OAuth erişimiyle `cloudcode-pa.googleapis.com` / `daily-cloudcode-pa.googleapis.com` üzerindeki `loadCodeAssist` ve kota yöntemleri çağrılır. Ücretli plan bilgisi ve kalan kota oranları yanıtın `paidTier`, `groups[].buckets[]`, `remainingFraction` ve `resetTime` alanlarından eşlenir.
3. Yanıt önce `retrieveUserQuotaSummary`, gerekirse `retrieveUserQuota` ve `fetchAvailableModels` kaynaklarından değerlendirilir. Yalnız Gemini grupları gösterilir; servis beş saat/hafta penceresi vermiyorsa o satır eklenmez.
4. Token reddedilirse veya süresi dolarsa eski snapshot korunur ve kullanıcının Antigravity uygulamasında yeniden oturum açması istenir. Gizli OAuth client değerlerini yerel ikiliden çıkarma ve token dosyasını değiştirme V1 kapsamına alınmaz.
5. Gemini token geçmişi yalnız doğrulanmış Gemini CLI oturum JSONL dosyalarından okunur. Antigravity günlüklerinin token şeması doğrulanmadığı için bu kaynakta token geçmişi gösterilmez.

## 9. Güvenlik, gizlilik ve güvenilirlik

- Varsayılan tamamen yerel çalışma; analitik ve kullanıcı hesabı yok.
- Kullanıcıdan sağlayıcı parolası, web cookie'si veya OAuth dosyasını elle kopyalaması istenmez. Etkinleştirilen bağlayıcı, CLI'ın zaten tuttuğu yerel oturum kaydını okur.
- Kota istekleri kullanıcının bilgisayarından doğrudan sağlayıcı hostlarına gider; araya ürün sunucusu girmez. OAuth erişim/yenileme tokenları snapshot, günlük veya tanılama kaydına yazılmaz.
- Bağlayıcı açıkça etkinleştirildiğinde yalnızca yukarıda belirtilen kullanım verisi endpointleri çağrılır. Bu endpointler uygulamanın veri kaynağı olarak etiketlenir; genel/resmî kararlı API oldukları iddia edilmez.
- Yerel token geçmişi kota izlemeden bağımsızdır; açıkça etkinleştirilir ve sağlayıcı başına açılıp kapatılır. İlk tarama öncesi kayıt türü, alınan alanlar, yerel saklama ve silme seçeneği anlatılır. Kota snapshot geçmişi otomatik senkronun parçasıdır.
- Oturum günlükleri kullanıcı izniyle yerelde salt okunur taranabilir; parser desteklenen kullanım sayaçlarını çıkarır. Prompt/yanıt, kod, dosya yolu, proje adı ve oturum kimliği gösterime, önbelleğe veya loga alınmaz. Kalıcı özet gün/model/token sayaçlarını; deduplikasyon için hashli kimlikleri/cursorları ve Gemini mesaj zamanlarını tutar.
- Kullanım geçmişinde varsayılan saklama 90 gündür; kullanıcı 30 veya 365 gün seçebilir ya da geçmişi hemen silebilir. Kota snapshot saklama ayarı bundan bağımsızdır.
- Fiyat listesi indirmesi yalnız model fiyat verisini alır; kullanıcı token kullanımı, model etkinliği veya hesap bilgisi göndermez. İstek kapatılabilir; çevrimdışı durumda katalog tarihi gösterilir.
- Tanılama günlüklerinde token, e-posta, hesap kimliği, ham API yanıtı, transcript ve prompt bulunmaz.
- Uygulama günlüğü normalde kısa saklanır; dışa aktarım önizlemeden sonra kullanıcı eylemiyle yapılır.
- Sağlayıcı 429/Retry-After veya benzer bekleme döndürürse o süreye uyulur; üstel geri çekilme ve jitter kullanılır.
- Başlangıçta önce son snapshot gösterilir, sonra canlı yenileme gelir. Son başarılı ölçümün zamanı hiçbir zaman yeniden yazılarak “şimdi” yapılmaz. Beş dakikalık kota snapshotları 365 gün tutulur; ayrı kota saklama ayarı henüz arayüzde yoktur.
- Tek bir sağlayıcı çökünce diğer sağlayıcıların kartları, paneli ve tepsi menüsü çalışmaya devam eder.
- Ayarlar için tek yazıcı, atomik dosya güncellemesi, sürümlü şema ve geri alınabilir yükseltme.
- Güncelleme paketi SHA-256 doğrulamasıyla indirilir; yayınlanmış kurulum imzalı olur. Güncelleme kontrolü ayarlardan kapatılabilir.

## 10. Yenileme ve veri kuralları

- Başlangıç yenilemesi: uygulama açılır açılmaz bir kez.
- Normal aralık: beş dakika; uygulama açıldığında ve panel açılınca ayrıca yenile. Sağlayıcı 429/Retry-After döndürürse daha geç tekrar et.
- Kota snapshotları her başarılı otomatik senkron sonunda yerel geçmişe eklenir. Snapshot geçmişi token geçmişi değildir; grafikte örnek zamanı ve yüzde değişimi gösterilir.
- Yerel token geçmişi yalnız kullanıcı etkinleştirdiyse ve artımlı biçimde taranır; canlı kota yenilemesinden ayrıdır. Geçmiş kapalıysa CLI oturum günlüklerine dokunulmaz.
- Fiyat kataloğu açıksa en fazla 24 saatte bir güncellenir; son bilinen fiyatla hesap yapılıyorsa katalog tarihi yanında görünür.
- “Şimdi yenile” her etkin sağlayıcı için tek isteği sıraya koyar; aynı anda yinelenen istekleri birleştirir.
- Çevrimdışı durumda önbellek kalır, fakat canlılık işareti ve okuma yaşı açık görünür.
- Pencere adları sağlayıcı yanıtından gelir; 5 saat ve 7 gün sabit varsayılmaz.
- Resmî kota yanıtı model kapsamlı haftalık pencereler verirse ayrı model satırı gösterilir; vermezse model kotası uydurulmaz.
- Reset zamanı UTC saklanır, Windows yerel saat dilimine çevrilir.
- Yüzde sağlayıcı alanından veya sağlayıcının aynı yanıtındaki kullanılan/payda alanlarından hesaplanır. Payda yoksa yüzde üretilmez.
- Canlı kota, API token sayısı, tahmini maliyet ve per-session context birbirinden ayrı metric türleridir.
- Tüm sağlayıcıların yüzdesi toplanmaz veya ortalanmaz. “En kritik durum” varsa yalnız karar verilebilir canlı kotalar arasında seçilir ve sağlayıcı adıyla belirtilir.

## 11. Yol haritası

### Aşama 0 — Kaynak doğrulama

- Codex CLI auth şemasını, quota yanıt pencerelerini ve Windows profil yolunu doğrula; yalnız abonelik OAuth oturumunu kabul et.
- Gemini CLI/Antigravity oturum kaynaklarını, Windows Credential Manager erişimini ve kota yanıtında gerçekten bulunan pencere türlerini doğrula.
- Endpoint şeması değiştiğinde son snapshotı koru, canlı veri etiketi kaldır ve hata kaynağını token içermeyen tanılamada göster.
- Gemini CLI günlük şemasını doğrula; 5 saat/hafta gibi kota alanları ile yerel token sayaçlarını ayrı tut.

### Aşama 1 — Ürün kabuğu

- Tek örnek uygulama, tepsi ikonu, flyout, ayarlar, yerel cache, güncelleme tercihi.
- Windows girişinde başlatma/kapama, tekrar başlatma ve Explorer kapanıp açılma akışı.

### Aşama 2 — ChatGPT/Codex ve Gemini kota senkronu · ilk kod tamamlandı

- Her iki bağlayıcı için mevcut yerel CLI oturumundan tokenı oku; geliştirme API anahtarı isteme.
- Codex rate limit pencerelerini ve Gemini quota bucketlarını ortak modele eşle; pencere/reset alanını sağlayıcı yanıtından koru.
- Açılışta, panel açılınca ve beş dakikada bir eşitle; son başarılı snapshotı yerel JSON/JSONL kaydına yaz ve hatada eski olarak sun. Gerçek Windows davranışı henüz derlenip doğrulanmadı.
- Giriş yok, token reddedildi, 429, çevrimdışı, kısmi alan ve endpoint şeması değişikliği durumlarını ayrı göster.

### Aşama 2B — Ayrıntılı yerel kullanım · ilk kod tamamlandı

- Kullanıcı izni, sağlayıcı başına aç/kapat, 30/90/365 günlük saklama seçimi ve geçmişi silme akışı eklendi.
- Codex ve Gemini CLI oturum JSONL dosyalarındaki token sayaçları artımlı okunur. Antigravity token günlükleri kapsam dışıdır.
- Token geçmişi için dönem/sağlayıcı filtreleri, zaman grafiği ve model dökümü eklendi; kota snapshot serisi ayrı tutulur fakat henüz grafiğe bağlanmamıştır.
- API liste fiyatı eşdeğeri doğrulanmış fiyat kataloğu olmadığı için ertelendi; Windows arayüzü abonelik/fatura maliyeti üretmez.
- Büyük dosyalar sınırlı bellekle işlenir; kullanıcı toplama iznini kapatabilir veya geçmişi silebilir.

### Aşama 3 — Görev çubuğu kenar şeridi ve düşük kota bildirimleri · ilk kod hazır

- AppBar kaydı ve primary-screen konumlandırması; çoklu ekran ve otomatik gizleme/tam ekran doğrulaması bekliyor.
- %20/%10 kalan eşikleri reset penceresi başına tekilleştirilir; tepsi menüsünden bildirimler kapatılabilir.
- Sessiz saatler ve kullanıcı tanımlı eşikler henüz yok.

### Aşama 4 — Paketleme ve dayanıklılık

- Endpoint reddi, oturum süresi dolması, uyku/uyanma ve 429 sonrasında eski veriyi doğru etiketle.
- Kullanıcı token/oturum dosyalarını değiştirmeden yeniden giriş yolunu ve açık sağlayıcı ayarlarını tamamla.
- Kaynaklardan alınan veri şeması değişikliklerini sürüm notları ve connector tanılamasında anlaşılır göster.

### Aşama 5 — Kurulum ve pilot

- Taşınabilir paket, yönetici izni gerektirmeyen kurulum, imza/checksum, kaldırma ve ayarları koruma.
- Farklı Windows ölçeklendirmesi, ekran yönü, görev çubuğu kenarı, autohide, çoklu monitör, uyku/uyanma ve tam ekranda pilot.
- Gerçek kullanıcı verisi yerine kayıtlı/sahte sağlayıcı yanıtlarıyla veri sözleşmesi ve UI doğrulama.

## 12. Kabul ölçütleri

### Ürün davranışı

- Uygulama tepside çalışır, pencere kapatılınca gizlenir; menüden tam çıkış mümkündür.
- Tek örnek kuralı ve oturum açılışında başlatma çalışır.
- ChatGPT/Codex, Gemini ve Claude pencereleri yalnız sağlayıcı yanıtının verdiği yüzdeler ve reset bilgileriyle gösterilir.
- API key oturumu veya kullanılamayan CLI oturumu abonelik kotası varmış gibi sunulmaz.
- Beş dakikalık sync başarılı snapshotları yerel geçmişe ekler; çevrimdışı/oturum hatasında eski veri yaşı korunur.
- Gemini'de her yanıtın vermediği 5 saatlik/haftalık satır boş kalır; dönen model penceresi kendi adıyla görünür.
- Token geçmişi kapalıysa CLI günlükleri okunmaz. Açıkken yalnız doğrulanmış sayaçlar kalıcılaşır; ham konuşma içeriği tutulmaz ve geçmiş silinebilir.
- Kullanım grafiği seçilen dönem ve sağlayıcıya göre doğru snapshot/token serisini gösterir; bu seriler karıştırılmaz.
- Bir sağlayıcının hatası diğerinin verisini veya panelini bozmaz.
- Son başarılı ölçüm zamanı hata sonrasında değiştirilmez; eski snapshot “canlı” diye gösterilmez.

### Windows ve kalite

- Windows 11'de tepsi/panel, Explorer restart, DPI %100–200, sleep/resume, full-screen ve taskbar autohide davranışı korunur.
- AppBar kapatıldığında ekran çalışma alanı eski boyutuna döner.
- Hedef performans: boşta ortalama CPU <%0,5; çalışma seti <150 MB; soğuk başlangıç <3 saniye.
- Yüksek kontrastta ve yalnız klavyeyle ana akışlar kullanılabilir.
- Tokenlar, e-posta, ham sağlayıcı yanıtı ve kullanıcı günlükleri tanılama kayıtlarına yazılmaz.

## 13. Kalan ürün kararları

Bağlantı yöntemi kullanıcı tarafından netleştirildi: kota için Windows Codex ve Antigravity uygulamalarının yerel oturumları kullanılacak; CLI veya geliştirici API anahtarı kurulmayacak. Token geçmişi istenirse ayrıca CLI günlüklerinden okunur ve varsayılan kapalıdır. Kota yanıtındaki pencereler plan/hesaba göre değişebilir. Antigravity token günlükleri ve tüm ChatGPT sohbetleri kapsam dışıdır. Claude token geçmişi yalnız Claude Code oturumlarını kapsar; Claude.ai web/masaüstü sohbetlerini saymaz. Claude kota endpointi belgelenmemiştir. Kalan doğrulama Windows derlemesi, gerçek abonelik oturumları, AppBar/DPI/çoklu ekran davranışı ve kota endpointlerinin değişimidir.

## 14. Kaynaklar

### Resmî sağlayıcı ve Windows belgeleri

- OpenAI, [Using Codex with your ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan)
- OpenAI, [Codex App Server docs](https://learn.chatgpt.com/docs/app-server) ve [kota yanıtı protokol şeması](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/schema/typescript/v2/GetAccountRateLimitsResponse.ts)
- Anthropic, [Claude Code commands](https://code.claude.com/docs/en/commands), [Monitoring](https://code.claude.com/docs/en/monitoring-usage), [Manage costs effectively](https://code.claude.com/docs/en/costs) ve [Messages usage report API](https://docs.anthropic.com/en/api/admin-api/usage-cost/get-messages-usage-report)
- Anthropic, [Claude Code authentication](https://code.claude.com/docs/en/authentication), [sessions and local JSONL files](https://code.claude.com/docs/en/sessions) ve [environment variables](https://code.claude.com/docs/en/env-vars). Oturum kayıtlarının JSONL şeması uygulama içindir ve sürümler arasında değişebilir.
- Google, [Gemini CLI quotas and pricing](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/quota-and-pricing.md), [Gemini CLI FAQ](https://github.com/google-gemini/gemini-cli/blob/main/docs/resources/faq.md), [/stats model komutu](https://github.com/google-gemini/gemini-cli/blob/main/docs/reference/commands.md), [Gemini API billing](https://ai.google.dev/gemini-api/docs/billing) ve [Cloud Billing export](https://docs.cloud.google.com/billing/docs/how-to/export-data-bigquery)
- Microsoft, [Application desktop toolbars / AppBar](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars) ve [SHAppBarMessage](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage)
- [KDE Agents Usage](https://github.com/yusufipk/kde-agents-usage): [README](https://github.com/yusufipk/kde-agents-usage/blob/main/README.md), [Limitler/Kullanım sekmeleri](https://github.com/yusufipk/kde-agents-usage/blob/main/package/contents/ui/FullRepresentation.qml), [grafik/model dökümü](https://github.com/yusufipk/kde-agents-usage/blob/main/package/contents/ui/TokensView.qml), [yerel sayaç toplayıcı](https://github.com/yusufipk/kde-agents-usage/blob/main/package/contents/code/token_stats.py), [kota/auth uygulaması](https://github.com/yusufipk/kde-agents-usage/blob/main/package/contents/code/fetch_usage.py), [MIT lisansı](https://github.com/yusufipk/kde-agents-usage/blob/main/LICENSE).
- [Usage Monitor Windows](https://github.com/sb-git-cs/usage-monitor): [Codex quota adapter](https://github.com/sb-git-cs/usage-monitor/blob/main/src/adapters/codex.js), [Gemini quota adapter](https://github.com/sb-git-cs/usage-monitor/blob/main/src/adapters/gemini.js), [Windows CLI/credential paths](https://github.com/sb-git-cs/usage-monitor/blob/main/src/paths.js), [MIT lisansı](https://github.com/sb-git-cs/usage-monitor/blob/main/LICENSE).
- [Claude quota widget (topluluk uygulaması)](https://github.com/DdeDamian/claude-quota-widget): OAuth kota yolu için incelenen tersine mühendislik örneği. `api.anthropic.com/api/oauth/usage` Anthropic'in kararlı, belgelenmiş uygulama API'si değildir; uygulama içi kullanım riski taşıdığı için bağlantı durumu ve kaynak arayüzde açıklanır.
- [LiteLLM açık model fiyat kataloğu](https://github.com/BerriAI/litellm/blob/main/model_prices_and_context_window.json): isteğe bağlı API fiyatı eşdeğeri için; gerçek fatura veya abonelik maliyeti değildir.

### Önceki yerel ürün bilgisi

- [Sağlayıcı kaynaklı Jev ve Gemini kullanım verisi](../../knowledge/provider-usage-billing-sources-20260923.md): token kullanımı, maliyet tahmini, sağlayıcı harcaması ve bakiye farklı ölçümlerdir; sağlanmayan veri uydurulmamalı.
