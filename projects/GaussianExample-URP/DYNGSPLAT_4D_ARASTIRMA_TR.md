# DynGsplat / 4D Splat Oynatma — WebGPU (aras-p) Projesi için Araştırma ve Entegrasyon Planı

> **Belge amacı:** [HiFi-Human/DynGsplat-unity](https://github.com/HiFi-Human/DynGsplat-unity)'in tekniğini bu projeye — **aras-p tabanlı WebGPU/WebGL + Android XR** viewer'ına — dinamik (4D) splat oynatma eklemek için nasıl kullanabileceğimizin dürüst analizi ve adım adım planı.
> **Proje:** `UnityGaussianSplatting_WebGPU_Export/projects/GaussianExample-URP`
> **Dil:** Türkçe · **Tarih:** 2026-07-01 · **Durum:** Araştırma + plan (kod yazılmadı)
> **Lisans hatırlatması (master brief Part 4):** DynGsplat **yalnızca referans** — kodu bu repoya kopyalanmaz. Bu belge davranış/teknik anlatır, kod değil fikir aktarır.
> **İlgili belgeler:** [`GaussianSplattingViewer.md`](GaussianSplattingViewer.md) (ZAUBAR master brief) · [`IMPROVEMENT_PLAN.md`](IMPROVEMENT_PLAN.md) · [`GSPLAT_TOUR_XR_SETUP.md`](GSPLAT_TOUR_XR_SETUP.md) · [`../../readme.md`](../../readme.md)

---

## 0. TL;DR — bu projeye özgü kritik bulgular

1. **Bu proje aras-p/UnityGaussianSplatting'in WebGPU fork'u.** GsplatXR'dan (wuyize25/gsplat-unity tabanlı) **tamamen ayrı bir kod tabanı**. Renderer: **CPU + octree tabanlı sıralama** (WebGL'de native pthread, masaüstünde C# Task), **hiç compute shader yok** (WebGPU uyumu için bilinçli olarak çıkarılmış). Yalnızca **statik** splat oynatıyor.

2. **DynGsplat'in çizim temeli bu projede çalışmaz.** DynGsplat, `wuyize25/gsplat-unity`'nin üstüne kuruludur ve o renderer GPU radix-sort için **wave/subgroup** işlemleri ister — gsplat'in kendi README'sine göre **Unity WebGPU bunları desteklemez**. Zaten bu proje tam da bu yüzden aras-p + CPU-sort yolunu seçmiş. Yani DynGsplat'i "kur ve çalıştır" seçeneği burada **yok**.

3. **DynGsplat'in decode'u da compute shader.** `DynGsplatDecoder.compute`, codebook→SH açılımını ve residual uygulamasını GPU compute'ta yapar. Bu projede **hiç compute yolu yok** (WebGPU'da yok). Dolayısıyla decode ya **CPU'da** (WASM thread'leri) ya da tamamen yeniden tasarlanarak yapılmalı.

4. **Mimari, kare-başına splat değişimine "düşman".** (Ajan analizi, kanıtlarla §3–§4):
   - **Octree bir kez, statik pozisyonlardan** kuruluyor (yükleme anında, ~800k splat için yüzlerce ms). Artımlı/refit yeniden-kurma yok — pozisyonlar her kare değişirse octree'yi baştan kurmak gerekir → **30 fps'de olanaksız**.
   - **Sıralama statik bir pozisyon anlık-görüntüsünden** okuyor ve önbelleği yalnızca **kameranın** hareketine göre geçersiz kılıyor (splat'ların kıpırdadığını *bilmiyor*).
   - Splat verisi **sıkıştırılmış + chunk-bazlı + Morton-döşeli Texture** olarak saklı; bir splat'ın pozisyon/rengini değiştirmek yeniden-kodlama + chunk-bounds yeniden-hesabı + Morton-yazma gerektiriyor.
   - Kod tabanında **hiçbir animasyon/zaman/sequence/delta kavramı yok** (kapsamlı arama sonucu).

5. **Sonuç:** 4D bu projeye eklenebilir, ama bu **küçük bir yama değil, paralel bir render yolu** açmaktır: octree'yi baypas eden, **sıkıştırılmamış dinamik buffer** kullanan, **CPU'da decode** eden, **her kare canlı pozisyonlara göre sıralayan** ayrı bir dinamik renderer.

6. **Stratejik soru (önemli):** ZAUBAR master brief'i ([`GaussianSplattingViewer.md`](GaussianSplattingViewer.md)) 4D için **compute shader'lı** bir yol (M3: "ApplyDeltas compute") ve **wuyize25 fork'u** öngörüyor. Bu iki varsayım da bu WebGPU projesinde geçersiz. Dolayısıyla karar verilmeli: **4D bu projeye mi ekleniyor** (zor yol, ama web/çoklu-platform kazanımı), yoksa **4D GsplatXR'da mı kalıyor** (compute var, wuyize25 var, `GsplatXR.Sequence` zaten yazılmış) ve **WebGPU projesi statik-web viewer** olarak mı bırakılıyor?

---

## 1. Bağlam — iki ayrı kod tabanı ve bu projenin yeri

ZAUBAR'ın splat viewer vizyonu tek ([`GaussianSplattingViewer.md`](GaussianSplattingViewer.md) = master brief, M0–M4, statik→4D). Ama pratikte **iki ayrı Unity kod tabanı** var:

| | **GsplatXR** | **Bu proje (WebGPU_Export)** |
|---|---|---|
| Renderer temeli | `wuyize25/gsplat-unity` (fork) | `aras-p/UnityGaussianSplatting` (WebGPU fork) |
| Sıralama | **GPU** radix-sort (wave ops) | **CPU** octree + thread'li mesafe sortu |
| Compute shader | Var (Vulkan) | **Yok** (WebGPU için çıkarılmış) |
| Hedef | XREAL Aura (Android XR, Vulkan) | **WebGL/WebGPU (tarayıcı)** + Android XR |
| 4D durumu | `GsplatXR.Sequence` yazılmış (keyframe+delta) | **Hiç yok** (statik) |
| Asset | SPZ v4 + kendi codec'i | aras-p `GaussianSplatAsset` (chunk'lı, sıkıştırılmış `.bytes`) |

Master brief renderer temelini "wuyize25/gsplat-unity'yi fork'la" diyor. **Bu proje o yolu izlemiyor** — çünkü wuyize25'in GPU-sort'u WebGPU'da çalışmıyor. Yani bu proje, master brief'in **web/WebGPU dağıtımı** için açılmış *alternatif* bir koludur ve mimari tercihleri (CPU-octree, compute'suz) doğrudan WebGPU kısıtından doğar. Bu, 4D tartışmasının çıkış noktası.

---

## 2. DynGsplat nedir? (teknik özet)

DynGsplat, dinamik gaussian dizilerini (insan "volumetrik videosu") oynatır. İki parça: **Python sıkıştırıcı** (`compress.py`) + **Unity paketi** (wuyize25 üstüne). DualGS makalesinin (SIGGRAPH Asia 2024) sıkıştırmasının **yalnızca ~1/3'ünü** — **renk/SH appearance codebook'unu** — uygular; **geometri (pozisyon/dönüş/ölçek/opaklık) ham** kalır.

**Aktarılabilir çekirdek fikirler (mimariden bağımsız):**
1. **Sabit splat sayısı + sabit index** tüm dizi boyunca (DualGS garantisi; master brief Part 3 de buna dayanıyor: "same splats every frame").
2. **Keyframe + seyrek delta:** blok başı bir anahtar-kare; sonraki kareler yalnızca *değişen* splat'lar için `(index, değer)`.
3. **Codebook (vector quantization):** renk/SH için sözlük; her splat tam SH yerine bir codebook-index'i taşır. Komşu kareler arası ~%1 index değişimi → yüksek sıkıştırma.
4. **GOP blokları (~20 kare) + 2-blok streaming penceresi:** RAM'de en çok 2 blok.

Bunlar master brief M3'ün istediği şeyle **birebir örtüşür**. Fark, bu fikirlerin *nasıl* çalıştırılacağı — ve orada bu projenin mimarisi devreye girer.

> Ayrıntılı DualGS/codebook/veri-pipeline anlatımı ve akademik bağlam için: GsplatXR'daki kardeş belge (`GsplatXR/docs/DYNGSPLAT_ARASTIRMA_TR.md`) referans alınabilir; teknik aynıdır, yalnızca hedef mimari farklıdır.

---

## 3. Bu projenin mimarisi (neyle çalışıyoruz)

Kaynak: `package/Runtime/` (aras-p türevi). Kanıtlar ajan taramasından, dosya yollarıyla.

**Renderer:**
- `GaussianSplatRenderSystem` (singleton) + `GaussianSplatRenderer` (MonoBehaviour, splat başına). `package/Runtime/GaussianSplatRenderer.cs`.
- Splat verisi buffer'ları **bir kez** oluşturulup `SetData` ile yükleniyor (`CreateResourcesForAsset`):
  - `m_GpuPosData` (sıkıştırılmış pozisyon), `m_GpuOtherData` (rot 10.10.10.2 + scale + SH index), `m_GpuSHData`, `m_GpuColorData` (**Morton-döşeli Texture2D**!), `m_GpuChunks` (256'lık chunk min/max).
- Çizim: tek `DrawProcedural` (splat başına instance, 6-index quad). Görünür index'ler octree'den gelen `_SplatIndexMap` ile eşleniyor.
- URP entegrasyonu: `GaussianSplatURPFeature` (`ScriptableRendererFeature`), `BeforeRenderingTransparents`, ayrı RT + composite. XR'da temporal filter kapalı (per-eye ghosting'i önlemek için).

**Asset formatı** (`GaussianSplatAsset`):
- Beş kanal **opak sıkıştırılmış `.bytes`** (TextAsset) olarak: pos/other/color/SH/chunk. `kChunkSize=256`, Morton yeniden-sıralama import'ta.
- Sıkıştırma: pozisyon `Norm11`, ölçek `Norm6`, renk `Norm8x4`, SH `Cluster`. **Chunk-göreli lerp** ile açılıyor (hem CPU hem shader'da).
- **Import editör-only:** `Tools/Gaussian Splats/Create GaussianSplatAsset`. Çalışma-anı asset üretim yolu **yok**. SPZ v2/v3 + PLY okuyucu var (`SPZFileReader.cs`, `PLYFileReader.cs`).

**Octree + CPU sort** (bu fork'un ayırt edici değişikliği, `package/Runtime/GaussianSplatOctree.cs`):
- **Kuruluş:** yükleme anında; pozisyonlar CPU'da açılıp `m_AllPositionsNative` (statik anlık-görüntü) içine snapshot'lanır; recursive subdivide; yaprakta orijinal splat index'leri. Tek-thread, tüm splat'lara birkaç kez dokunur → **yükleme-anı operasyonu**.
- **Kare-başı sort:** frustum-cull → görünür düğümleri mesafeye göre sırala → düğüm içi splat'ları thread'lerle sırala → `m_VisibleIndicesBuffer`'a yükle. Önbellek: düğüm, kamera o düğüme göre ~25° dönene kadar yeniden sıralanmaz.
- **Compute yok:** `package/`'ta hiç `.compute` yok. `GpuSorting.cs` ölü kod. Tüm cull/sort CPU'da.

**WebGPU/WebGL kısıtları:**
- Grafik API: Android=Vulkan, iOS=Metal, Web=Unity WebGPU/WebGL cihazı. WebGL thread'leri açık (`webGLThreadsSupport:1`, SharedArrayBuffer/CORS gerekir), `webGLMemorySize:16` (düşük placeholder — 800k splat için yükseltilmeli).
- **Compute / wave-op yok** (bilinçli). Yalnızca vertex/fragment + `ByteAddressBuffer.Load`.

---

## 4. Kritik uyumsuzluk analizi — 4D neden bu mimariye "düşman"?

Ajanın en derin bulgusu. Dört bağımsız engel, her biri "splat'lar sabit" varsayar:

| Engel | Statik varsayım | 4D için maliyet |
|-------|-----------------|-----------------|
| **Octree kuruluşu** | Bir kez, statik pozisyonlardan; "static splats" yorumu; snapshot | Her kare tam yeniden-kurma → **olanaksız** (~800k, tek-thread, O(N log N)) |
| **Sort mesafesi + önbellek** | Statik snapshot'tan okur; önbellek yalnızca kameraya göre | Her kare snapshot tazele + önbelleği geçersiz kıl → pahalı, üstelik octree hâlâ bayat |
| **Sıkıştırılmış/chunk/Morton buffer** | Bir kez yüklenir, paketli+chunk-göreli+döşeli | Her kare yeniden-kodla + yeniden-yükle → pahalı; muhtemelen **yeni format** şart |
| **Zaman/sequence kavramı** | Yok | Sıfırdan kurulmalı |
| **Compute decode (DynGsplat)** | — | WebGPU'da compute yok → decode CPU'ya taşınmalı |
| **wuyize25 renderer (DynGsplat temeli)** | — | WebGPU'da hiç çalışmaz (wave ops) |

**Özet:** DynGsplat'in *hem* renderer'ı *hem* decode'u compute'a dayanır; bu proje ise compute'suz WebGPU için tasarlanmış. Ayrıca octree ve sıkıştırılmış-chunk asset modeli, kare-başına değişimi pahalı kılar. Yani DynGsplat'ten **runtime aktarımı imkânsıza yakın**; yalnızca **fikir** (keyframe+delta+codebook+GOP) aktarılabilir ve bu fikirler **bu mimariye uygun, compute'suz, octree-baypaslı yeni bir yolla** hayata geçirilmeli.

---

## 5. Master brief M3 ile ilişki (strateji için)

[`GaussianSplattingViewer.md`](GaussianSplattingViewer.md) Milestone 3, 4D'yi şöyle tarif ediyor:
> *"a compute shader (ApplyDeltas) scatter-writes sparse deltas onto the resident buffers … swap on the frame tick; resort every frame … timeline API."*

Bu tarif **compute shader** ve **her kare GPU sort** varsayar — ikisi de bu WebGPU projesinde yok. Yani master brief M3, **wuyize25/GsplatXR yolunu** kastediyor; WebGPU projesine olduğu gibi uygulanamaz. Bu, aşağıdaki stratejik seçimi zorunlu kılar.

---

## 6. Entegrasyon stratejileri (WebGPU'ya özgü)

### Strateji A — ✅ En gerçekçi: Ayrı "dinamik splat" render yolu (octree baypası)

**Fikir:** Statik sahne (salon) mevcut octree yolunda kalsın. **Performansçı** ayrı bir `GaussianSplatRenderer`-benzeri bileşende, **octree kapalı** (`m_EnableOctreeCulling=false`), **sıkıştırılmamış** dinamik buffer'larla, **CPU'da decode**, **her kare basit sort** ile çizilsin.

**Neden uygun:** Master brief'in "performansçıyı statik salona bindir" vizyonuyla örtüşür; dinamik içerik splat sayısı görece küçük (~100–200k) → octree/sıkıştırma olmadan da yönetilebilir; WebGPU'da compute gerektirmez.

**Gerekenler:**
1. **Yeni dinamik buffer formatı:** Float32 pozisyon/renk (sıkıştırmasız), `SetData` ile kare-başı güncellenebilir. aras-p'nin paketli/chunk/Morton yolunu bu içerik için baypas et.
2. **CPU decode:** `.zsplat4d` bloklarını (keyframe + seyrek delta + blok-codebook'u) WASM thread'lerinde çöz; keyframe'den baseline, delta'ları uygula, codebook'tan renk/SH aç. (DynGsplat'in `compress.py`/`DynGsplatDecoder.compute`'u **referans**.)
3. **Kare-başı sort:** Dinamik splat'lar için canlı pozisyonlara göre basit paralel mesafe sortu (fork'un WASM pthread altyapısı uyarlanabilir; octree-önbelleği DEĞİL). Ya da alternatif: **stochastic** blend (fork'ta zaten mevcut, sort'suz) — kaliteyi ölç.
4. **Sequence/timeline:** ring buffer (2 blok) + play/pause/seek/loop + enjekte edilebilir saat (master brief M3).

**Maliyet:** Yüksek ama sınırlı — paralel bir yol, mevcut statik yolu bozmadan.

### Strateji B — Çift-hedefli decode (Android XR compute + WebGPU CPU)

**Fikir:** Bu proje **hem** WebGL/WebGPU **hem** Android XR (Vulkan) hedefliyor. Android/Vulkan'da compute **var**. İki decode yolu: Vulkan'da compute `ApplyDeltas` (master brief M3'e sadık, hızlı), WebGPU'da CPU decode (Strateji A). Ortak sequence/timeline katmanı.

**Neden düşünülür:** Cihazda (Aura) tam hızlı compute yolu; web'de taşınabilir CPU yolu.

**Maliyet:** Daha yüksek (iki decode implementasyonu + platform dallanması). Yalnızca her iki hedef de eşit önemliyse mantıklı.

### Strateji C — 🧭 Stratejik alternatif: 4D'yi GsplatXR'da tut, bu projeyi statik-web bırak

**Fikir:** 4D için doğal ev **GsplatXR** (compute var, wuyize25 var, `GsplatXR.Sequence` zaten yazılmış, master brief M3'e uygun). Bu WebGPU projesi **statik web/çoklu-platform viewer** olarak kalsın; 4D'yi buraya taşımanın maliyeti, kazancına değmeyebilir.

**Neden ciddiye alınmalı:** §4'teki engeller gerçek ve büyük. 4D'yi burada kurmak, fork'un temel yeniliklerini (octree, compute'suzluk) baypas etmek demek — yani projenin varlık sebebiyle çelişmek. Eğer 4D'nin web'de oynaması *zorunlu bir ürün hedefi değilse*, en verimli yol budur.

**Karar:** Bu, teknik değil **ürün** kararı. "4D tarayıcıda oynamalı mı?" sorusunun cevabı stratejiyi belirler (bkz. §7 açık sorular).

---

## 7. Adım adım plan (Strateji A önceliğiyle)

> Master brief'in "önce ölç, sonra tasarla" ilkesine uygun. Cihaz/tarayıcı ölçümleri gerçek donanımda yapılmalı (editör yanıltır). Hiçbir perf sayısı ölçülmeden yazılmaz (master brief Part 7).

### Faz 0 — Fizibilite ölçümü (kod minimum) · ~2-4 gün
**Amaç:** 4D'nin bu mimaride *mümkün* olup olmadığını sayılarla göster. Tasarımdan önce blokerleri ölç.
- [ ] **0.1** Octree kuruluş maliyetini ölç: mevcut ~800k Barangaroo asset'inde `Build()` süresi (masaüstü + WebGL). Bu, "her kare rebuild olanaksız" tezini rakamla doğrular.
- [ ] **0.2** WASM thread sort throughput'u ölç: ~150k splat'ı canlı pozisyonlardan kare-başı sıralamak WebGPU'da 33 ms'e sığıyor mu? (Fork'un native sort altyapısıyla mikro-bench.)
- [ ] **0.3** Sıkıştırmasız dinamik buffer `SetData` maliyetini ölç: 150k splat × (pos+color) her kare yüklemek WebGPU/mobil bant genişliğinde uygulanabilir mi?
- [ ] **0.4** DynGsplat'in Python `compress.py`'ını hazır DualGS "Bass" verisiyle çalıştır (masaüstü/CUDA) → gerçek sıkıştırma sayıları + bir altın-referans dizi. (Kod referans, kopyalanmaz.)
- [ ] **0.5** Bulguları raporla → Strateji A/B/C kararını besle.

**Çıktı:** "4D burada uygulanabilir mi, hangi bütçeyle?" sorusuna sayılı cevap.

### Faz 1 — Format ve mimari tasarım (spec) · ~2-3 gün
- [ ] **1.1** `.zsplat4d` blok formatını master brief Part 5'e göre netleştir (keyframe + seyrek delta + blok-codebook, LOD-rank'lı). Paketleme ekibiyle (Python/CLI) hizala.
- [ ] **1.2** Dinamik render yolu tasarımı: octree-baypaslı `DynamicGaussianSplatRenderer`, sıkıştırmasız Float32 buffer'lar, CPU decode + kare-başı sort. Mevcut `GaussianSplatRenderer`'dan neyi paylaşıp neyi ayıracağını belirle.
- [ ] **1.3** Sort stratejisi kararı: canlı-pozisyon CPU sortu mu, yoksa stochastic (sort'suz) mu? Faz 0.2 sonucuna göre.
- [ ] **1.4** Sequence/timeline API tasarımı (ring buffer 2 blok, play/pause/seek/loop, enjekte saat).

**Çıktı:** Onaylı format + dinamik render yolu tasarımı.

### Faz 2 — CPU decode + dinamik buffer yükleme · ~5-8 gün
- [ ] **2.1** `.zsplat4d` blok okuyucu + zstd decompress (native plugin — master brief libzstd).
- [ ] **2.2** CPU decode: keyframe baseline → seyrek delta uygula → codebook'tan renk/SH aç (WASM thread'lerinde). `DynGsplatDecoder.compute` mantığının **CPU** karşılığı.
- [ ] **2.3** Sıkıştırmasız dinamik buffer + kare-başı `SetData` yolu. 0-GC (steady-state) kuralına uy.
- [ ] **2.4** Ring buffer (2 blok) + async prefetch.

**Çıktı:** Editörde tek performansçı diziyi (statik salon olmadan) oynatan decode yolu.

### Faz 3 — Dinamik render yolu + sort + composite · ~5-8 gün
- [ ] **3.1** `DynamicGaussianSplatRenderer`: octree kapalı, dinamik buffer'ları çiz. Mevcut `GaussianSplats.shader` çizim yolunu paylaş (yalnızca veri kaynağı farklı).
- [ ] **3.2** Kare-başı sort (canlı pozisyon) veya stochastic. URP feature ile composite (mevcut RT yoluna otur).
- [ ] **3.3** Statik salon + dinamik performansçının birlikte doğru render/composite'i (render order, premultiplied alpha — see-through için).
- [ ] **3.4** Timeline entegrasyonu.

**Çıktı:** Statik sahneye bindirilmiş, oynayan 4D performansçı (editör/masaüstü).

### Faz 4 — Temporal LOD, test, cihaz/web doğrulaması · ~4-6 gün
- [ ] **4.1** Temporal LOD (master brief M3): mesafeye göre SH-delta düşür, yarım/çeyrek FPS + GPU lerp, LOD-rank düşür.
- [ ] **4.2** Golden-image + unit + allocation (0-GC) testleri.
- [ ] **4.3** **WebGPU tarayıcı doğrulaması:** gerçek tarayıcıda FPS/bellek; SharedArrayBuffer/CORS başlıkları; `serve.py`.
- [ ] **4.4** **Android XR (Aura) doğrulaması:** cihazda FPS/GPU-ms/termal; `DEVICE_PROFILE.md`'ye **ölçülmüş** rakamlar. (Strateji B seçilirse compute decode yolu burada.)
- [ ] **4.5** Stereo/sort cadence doğrulaması (per-eye).

**Çıktı:** Web + cihazda doğrulanmış 4D; ölçülmüş bütçe.

---

## 8. Karar noktaları / açık sorular

1. **[EN KRİTİK] 4D tarayıcıda (WebGPU) oynamak zorunda mı?** Evet ise Strateji A/B; hayır ise Strateji C (4D GsplatXR'da, bu proje statik-web). → **Ürün kararı, sen vermelisin.**
2. **Hedef önceliği:** WebGPU mu, Android XR (Aura) mı, ikisi de mi? Strateji B yalnızca "ikisi de" ise mantıklı.
3. **Dinamik splat bütçesi:** Performansçı kaç splat? (~100–200k varsayımı ölçülmeli.) Bu, octree-baypaslı sort'un uygulanabilirliğini belirler (Faz 0.2).
4. **Sort mu, stochastic mi?** WebGPU'da canlı-pozisyon CPU sortu bütçeye sığmıyorsa stochastic blend (kalite ödünü) → Faz 0 ölçümü.
5. **View-dependent SH cihazda/web'de gerekli mi?** Değilse codebook'un SH kısmına yatırım azalır (yalnızca DC renk).
6. **CUDA ortamı** var mı (Faz 0.4 için)? DualGS "Bass" verisiyle referans üretmek için gerekli.

---

## 9. Riskler

| Risk | Etki | Azaltma |
|------|------|---------|
| WebGPU'da kare-başı sort bütçeye sığmaz | 4D web'de akmaz | Faz 0.2 erken ölç; stochastic fallback; splat sayısını düşür |
| Octree-baypas → statik salonla yanlış blend/derinlik | Görsel hata | Ayrı render order + premultiplied alpha; dinamik içeriği salonun "önüne" yerleştir |
| Sıkıştırmasız dinamik buffer belleği (WebGL 16MB placeholder) | OOM / crash | `webGLMemorySize` yükselt; splat bütçesi; yalnızca 2 blok resident |
| İki kod tabanında (GsplatXR + WebGPU) paralel 4D | Bakım yükü | Strateji C'yi ciddi değerlendir; ortak format (`.zsplat4d`) tek kaynak |
| Compute'suz decode CPU'yu doyurur | FPS düşer | WASM thread havuzu; decode'u prefetch'e yay; blok-codebook ile iş azalt |
| DynGsplat lisansı | Hukuki | Yalnızca davranış/README referansı; kod kopyalanmaz (master brief Part 4) |

---

## 10. Öneri özeti

- **DynGsplat runtime'ı bu projeye taşınamaz** — çizimi ve decode'u compute'a dayanır, bu proje compute'suz WebGPU için tasarlanmış; ayrıca octree + sıkıştırılmış-chunk mimarisi kare-başı değişime düşman.
- **Aktarılabilir olan tekniktir:** keyframe + seyrek delta + codebook + GOP-streaming. Bunlar master brief M3 ile birebir örtüşür.
- **En gerçekçi teknik yol Strateji A:** octree-baypaslı, sıkıştırmasız, CPU-decode'lu, kare-başı-sort'lu **ayrı dinamik render yolu**. Statik salon mevcut octree yolunda kalır.
- **Ama önce §8.1'i cevapla:** 4D tarayıcıda oynamak zorunda mı? Değilse **Strateji C** (4D'yi compute'lu/wuyize25'li GsplatXR'da tutmak) çok daha az riskli ve master brief'in orijinal M3 tasarımına sadık.
- **İlk somut adım: Faz 0 ölçümleri** — octree build süresi, WASM sort throughput'u, dinamik buffer upload maliyeti + `compress.py` ile altın-referans. Bu sayılar A/B/C kararını verir.

---

## 11. Kaynaklar

**Repolar:** [DynGsplat-unity](https://github.com/HiFi-Human/DynGsplat-unity) · [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) · [gsplat-unity (wuyize25)](https://github.com/wuyize25/gsplat-unity) · [DualGS](https://github.com/HiFi-Human/DualGS) · [nianticlabs/spz](https://github.com/nianticlabs/spz)

**Makaleler:** [DualGS — arXiv:2409.08353](https://arxiv.org/abs/2409.08353) · [SqueezeMe (Quest 3, mobil tavan)](https://arxiv.org/html/2412.15171v1) · [V³ (mobil, 2D-video)](https://arxiv.org/html/2409.13648v2)

**Proje içi:** [`GaussianSplattingViewer.md`](GaussianSplattingViewer.md) (master brief) · [`IMPROVEMENT_PLAN.md`](IMPROVEMENT_PLAN.md) · [`GSPLAT_TOUR_XR_SETUP.md`](GSPLAT_TOUR_XR_SETUP.md) · [`../../readme.md`](../../readme.md) · [`../../docs/render-pipeline-integration.md`](../../docs/render-pipeline-integration.md)
```
