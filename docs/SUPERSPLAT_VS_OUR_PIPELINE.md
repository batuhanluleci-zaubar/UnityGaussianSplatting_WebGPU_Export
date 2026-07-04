# PlayCanvas SuperSplat vs Bizim Pipeline — Karşılaştırma & Uygulama Planı

> **Tarih:** 2026-07-04  
> **Amaç:** SuperSplat’ın Streamed SOG üretim + runtime davranışını resmi dokümanlar, `splat-transform` CLI ve `playcanvas/engine` `gsplat-unified` kaynak kodu ile karşılaştırmak; farkları önceliklendirip sonraki implementasyon için plan kaydetmek.  
> **Kaynaklar:** [Streamed SOG spec](https://developer.playcanvas.com/user-manual/gaussian-splatting/formats/streamed-sog/), [SuperSplat streaming](https://developer.playcanvas.com/user-manual/supersplat/streaming/), [LOD streaming guide](https://developer.playcanvas.com/user-manual/gaussian-splatting/building/lod-streaming/), [splat-transform README](https://github.com/playcanvas/splat-transform), `playcanvas/engine` `gsplat-octree-instance.js`.

---

## TL;DR

SuperSplat **aynı mimari fikri** kullanıyor: `lod-meta.json` + kd-tree leaf’ler + leaf başına **bağımsız LOD kopyaları** + mesafe bandı + global Gaussian budget + coarse-first progressive load + never-drop-visible swap.

Biz **~%70–80 aynı yoldayız** (SOG reader, kd-tree walk, ref-count loader, cooldown, unified pool taslağı). Fakat kritik farklar var:

1. **Offline LOD piramidi farklı üretiliyor** — SuperSplat önce **global decimate** (%50 → %25 → %10, k-NN pairwise merge), sonra chunk’lıyor; biz **önce chunk**, sonra **leaf-içi voxel merge**.
2. **SOG runtime path’te global budget balancer çalışmıyor** — `SogBudgetBalancer` / `GsplatLodScheduler.EvaluateVisible` SPZ path’te var, `UpdateSog()` içinde **hiç çağrılmıyor**.
3. **`lodUnderfillLimit` + `stagedPrefetch` SPZ’ye bağlı** — `SogStreamer` PlayCanvas `selectDesiredLodIndex` / `prefetchNextLod` mantığının sadece bir alt kümesini uyguluyor.
4. **Defaults SuperSplat’tan düşük** — `lodMultiplier` 3 (PC) vs 2 (prefab), `deviceBudget` 2–4M (PC) vs 400K, `veryNearFraction` 0.18 vs agresif near-instant LOD0 ihtiyacı.
5. **İlk kare stratejisi farklı** — SuperSplat tüm sahneyi **en kaba LOD** ile anında doldurur (`lodRangeMin/Max` coarse clamp + `frame:ready`); biz unified SOG’da **sadece frustum leaf’lerini** stream ediyoruz.

Bu belge **uygulama sırasını** tanımlar; kod değişikliği henüz yapılmadı.

---

## 1. SuperSplat ne yapıyor? (Resmi akış)

### 1.1 Upload / publish (otomatik)

| Gaussian sayısı | Format |
|-----------------|--------|
| < 1M | Tek monolithic `.sog` (stream yok) |
| ≥ 1M | **Streamed SOG**: `lod-meta.json` + `{lod}_{chunk}/` SOG klasörleri |

Arka planda **splat-transform**:
1. **Decimate** — progressive pairwise merging (`--decimate 50%`, `25%`, `10%` …) ile LOD1+ üretir; **LOD0 = kaynak dosyanın tamamı** (`-l 0`).
2. **Chunk** — tüm LOD seviyelerinin centroid’leri üzerinde median-split KD-tree; durma kuralı: `count ≤ 512K` **VE** `extent ≤ 16m` (`-C 512`, `-X 16`).
3. **SOG encode** — her chunk ayrı unbundled SOG (WebP + `meta.json`); splat’lar Morton order.
4. **Opsiyonel `env/`** — whole-scene coarse arka plan, **her zaman resident**.

Kaynak örnek (developer-site):

```bash
splat-transform source.ply -F 50% lod1.ply
splat-transform source.ply -F 25% lod2.ply
splat-transform source.ply -F 10% lod3.ply
splat-transform source.ply -l 0 lod1.ply -l 1 lod2.ply -l 2 lod3.ply -l 3 out/lod-meta.json
```

### 1.2 Runtime (PlayCanvas Engine `gsplat-unified`)

Her ~10 frame (veya kamera hareketi):

| Adım | Davranış |
|------|----------|
| **LOD seçimi** | Leaf AABB’ye en yakın nokta → FOV scale → `optimalLod = first i where dist < lodBaseDistance × lodMultiplier^i` |
| **Underfill** | `lodUnderfillLimit`: optimal ile optimal+N arasında **zaten yüklü en iyi** LOD’u göster; yoksa range içindeki en kaba |
| **Prefetch** | `prefetchNextLod`: optimal’a **frame başına bir seviye** in; coarse tüm sahne otursun, sonra refine |
| **Never-drop-visible** | Yeni LOD resident olana kadar eski slot tutulur (`pendingDecrements`) |
| **Global budget** | 64 √-distance bucket; over budget → **uzaktan degrade**, under → yakından upgrade; `budgetScale` damper (dead-zone 0.6–1.4, blend 0.3) |
| **Cooldown eviction** | ref=0 → N frame bekle → free |
| **Environment** | Tree’den bağımsız, unconditional draw |

**Device budget (SuperSplat Viewer):**

| Platform | Performance ON | Performance OFF |
|----------|----------------|-----------------|
| Desktop | 2M | 4M |
| Mobile/XR | 1M | 2M |

**Progressive first frame:** `lodRangeMax = coarsest` ile başla → `frame:ready` (coarse render bitti, loading=0) → full range aç, fine stream et.

**Varsayılan tuning:** `lodMultiplier = 3` (min 1.2), FOV otomatik kompanse.

---

## 2. Biz ne yapıyoruz?

### 2.1 Offline (`tools/gsplat_lod/sog_baker.py`)

| Adım | Bizim davranış |
|------|----------------|
| Kaynak | `.spz` oku |
| Global prune | `--raw-lod0` iken **yok** (2026-07-04 fix); eskiden aspect prune L0’ı da kesiyordu |
| Chunk | KD-tree, `lod-chunk-count × 1024` cap, `lod-chunk-extent` (Festsaal: 128×1024 / 6m → ~167 leaf) |
| LOD0 | `--raw-lod0`: leaf subset **ham** (merge yok) |
| LOD1+ | Leaf içinde `voxel_merge` (voxel × lodMult^level), opacity/aspect prune **sadece merge path’te** |
| Encode | SOG WebP + paylaşımlı shN VQ codebook |
| Env | Opsiyonel; Festsaal prefab’da `enableEnv=0` |

**Festsaal 200k (rebake sonrası):** L0 = **195.416** (kaynak), counts = `[195416, 107863, 76399, 38182, 14713]`.

### 2.2 Runtime (`GaussianLodStreamAsync` SOG path)

| Bileşen | Durum |
|---------|--------|
| `SogKdTree.WalkVisibleLeaves` | ✅ Frustum + leaf AABB |
| `SogStreamer.ApplyLodChanges` | ✅ Mesafe bandı, prefetch, never-evict |
| `SogChunkLoader` | ✅ Ref-count + 100-frame cooldown |
| `GpuBufferPool` + unified renderer | ✅ `useUnifiedRenderer=true` |
| `SogBudgetBalancer` | ⚠️ **Kod var, SOG UpdateSog’da kullanılmıyor** |
| `lodUnderfillLimit` | ⚠️ **Sadece SPZ Evaluate()** |
| `stagedPrefetch` | ⚠️ **Sadece SPZ Evaluate()** |
| Coarse-first whole scene | ❌ Frustum-only bootstrap |
| `environment` always-on | ❌ Kapalı (Festsaal) |
| Platform budget auto | ❌ Sabit inspector `deviceBudget=400K` |

**Prefab (`GaussianLodStreamAsync 1`):** `lodBaseDistance=8`, `lodMultiplier=2`, `veryNearFraction=0.18`, `deviceBudget=400000`, `maxResidentChunks=128`.

---

## 3. Yan yana fark tablosu

| Konu | SuperSplat / PlayCanvas | Bizim pipeline | Etki |
|------|-------------------------|----------------|------|
| **LOD0 içeriği** | Kaynak splat’ların tamamı (decimate edilmez) | `--raw-lod0` ile aynı (fix sonrası) | ✅ Hizalandı |
| **LOD1+ üretimi** | Global `--decimate N%` (k-NN graph, ~%50/%25/%10) | Per-leaf `voxel_merge`, voxel büyümesi | Orta — farklı splat dağılımı, farklı coarse görünüm |
| **Decimation algoritması** | `decimate.ts` progressive pairwise (KL cost) | `merge.py` voxel-grid moment matching | Orta — sayılar benzer ama identik değil |
| **Chunk stop rule** | 512K / 16m default | 128K / 6m (Festsaal) | Düşük–Orta — daha çok leaf, daha ince stream birimi |
| **SOG codebook** | k-means (splat-transform SOG writer) | Uniform log-range bins + dataset min/max | Düşük — küçük quant hatası |
| **lodMultiplier default** | 3 (min 1.2) | 2 (prefab) | **Yüksek** — L0 bandı dar, erken L2/L3 |
| **deviceBudget** | 1–4M (platform) | 400K | **Yüksek** — erken degrade / pool full |
| **Global budget balancer** | Her frame visible leaf’lerde | SOG path’te **atıl** | **Yüksek** |
| **lodUnderfillLimit** | Engine-native underfill | SPZ only | **Yüksek** — SOG’da hole / stuck coarse |
| **stagedPrefetch / prefetchNextLod** | Engine-native | Kısmi (`PrefetchTargetLod`) | Orta |
| **Coarse-first full scene** | `lodRange` + `frame:ready` | Yok (frustum-only) | **Yüksek** — uzaktan bakınca boş/kaba his |
| **Environment tier** | Always resident far field | Kapalı | Orta (interior sahnelerde env gerekmez) |
| **Renderer** | WebGPU gsplat-unified | URP + tek merged draw (pool) | Orta — farklı GPU path |
| **Sort** | Unified global sort (WebGPU) | Octree + radix (pool merge) | Orta |
| **İlk yükleme transport** | HTTP/CDN chunk fetch | StreamingAssets + WebP decode | Düşük (platform farkı) |
| **Needle/aspect prune bake** | Varsayılan yok (decimate kaliteyi taşır) | LOD1+ opsiyonel; L0 ham | ✅ L0 fix sonrası hizalı |

---

## 4. Kod kanıtı — kritik gap’ler

### 4.1 SOG path budget balancer bağlı değil

`GsplatLodScheduler.EvaluateVisible` yalnızca tanımlı; `UpdateSog()` içinde **çağrı yok**. `m_SogScheduler` sadece field sync + `budgetScale` okuma için kullanılıyor:

```975:982:projects/GaussianExample-URP/Assets/Scripts/Cinematic/GaussianLodStreamAsync.cs
            if (m_SogScheduler != null)
            {
                m_SogScheduler.lodBaseDistance = lodBaseDistance;
                m_SogScheduler.lodMultiplier = lodMultiplier;
                m_SogScheduler.deviceBudget = deviceBudget;
                m_SogScheduler.forceMaxQuality = forceMaxQuality;
                m_BudgetScale = m_SogScheduler.budgetScale;
            }
```

PlayCanvas’ta `evaluateOptimalLods` + `GSplatBudgetBalancer` birlikte çalışır.

### 4.2 `lodUnderfillLimit` SOG streamer’da yok

SPZ `Evaluate()` → `SelectUnderfillLevel_Diag()` kullanır. `SogStreamer.cs` içinde `lodUnderfillLimit` / `selectDesiredLodIndex` eşdeğeri **yok** — sadece `PrefetchTargetLod` + resident check.

PlayCanvas referans (`gsplat-octree-instance.js`):

- `selectDesiredLodIndex(node, optimal, maxLod, lodUnderfillLimit)` — loaded finest within `[optimal .. optimal+limit]`
- `prefetchNextLod(node, desired, optimal)` — one step finer per pass

### 4.3 Offline LOD piramidi farklı

SuperSplat: **decimate whole scene → tag LOD → combine/chunk**.  
Biz: **chunk whole scene → per-leaf voxel LOD**.  

Sonuç: Aynı `lod-meta.json` schema’sına rağmen **level başına splat sayıları ve spatial dağılım farklı**. SuperSplat L1 ≈ %50 global; bizim L1 leaf-voxel merge ile ~%55 (Festsaal rebake).

---

## 5. Uygulama planı (öncelik sırasıyla)

### Phase A — Runtime parity (1–2 gün, **yüksek ROI**)

| ID | İş | Dosyalar | Kabul kriteri |
|----|-----|----------|---------------|
| A1 | SOG visible leaf listesinde `GsplatLodScheduler` + `SogBudgetBalancer` çalıştır; `desiredLod` sonucunu `SogStreamer`’a feed et | `GaussianLodStreamAsync.UpdateSog`, `SogStreamer` | HUD: budget aşımında uzak leaf’ler L+1; `residentSplats ≤ deviceBudget` |
| A2 | `lodUnderfillLimit`’i `SogStreamer` / `SogAssemblyLod`’a port et (PlayCanvas `selectDesiredLodIndex`) | `SogStreamer.cs`, `GaussianLodStreamAsync.cs` | Optimal L0 yüklenirken en az L2 resident ise L2 gösterilir, hole yok |
| A3 | `prefetchNextLod` birebir: desired≠optimal iken **tek adım** finer prefetch; optimal resident değilse optimal’ı request et | `SogStreamer.PrefetchTargetLod` | Progressive refine tüm frustum leaf’lerde L4→L0 tamamlanır |
| A4 | SuperSplat defaults: `lodMultiplier=3`, `lodBaseDistance` scene extent’e göre auto; `veryNearFraction=0.4` interior profili | Prefab + `FrameCameraFromSogBounds` | İç mekân duvarları desired L0 |
| A5 | Platform budget preset: Desktop 2M, Editor test 1M, XR 750K–1M | `GaussianLodStreamAsync`, `GaussianSplatSettings` | Aura/macOS’ta pool full uyarısı yok |
| A6 | `budgetScale` damper’ı SOG eval loop’a bağla (dead-zone + blend, PC `world.js` parity) | `GsplatLodScheduler`, `UpdateSog` | Kamera durunca LOD osilasyonu yok |

### Phase B — Progressive first frame (0.5–1 gün)

| ID | İş | Kabul kriteri |
|----|-----|---------------|
| B1 | Play başında `forceCoarseBootstrap=true`: tüm leaf’ler için `desiredLod=max`, assembly queue coarse-first | Play ilk 200ms’de tam sahne silhouette |
| B2 | Bootstrap bitince (`SogIsStreamingFill()==false` veya timeout) normal LOD range | SuperSplat `lodRangeMin/Max` + `frame:ready` analog |
| B3 | Opsiyonel: off-frustum leaf’ler için en kaba LOD pin (memory izin veriyorsa) | Orbit kamera boşluk yok |

### Phase C — Offline bake parity (2–3 gün)

| ID | İş | Kabul kriteri |
|----|-----|---------------|
| C1 | `sog_baker.py`’ye **global decimate path** ekle: `--supersplat-lod-pyramid` → splat-transform benzeri %50/%25/%10 (veya auto level count) **sonra** chunk | `counts[1]/counts[0] ≈ 0.5`, schema aynı |
| C2 | Alternatif: bake script’i `@playcanvas/splat-transform` CLI’yi wrap et (`rebake_via_splat_transform.sh`) | SuperSplat editor asset ile byte-identical manifest yapısı |
| C3 | Chunk defaults profili: `Desktop 512K/16m`, `Mobile 128K/6m`, `Festsaal200k 128K/6m` | Dokümante preset |
| C4 | SOG codebook: opsiyonel k-means (`--kmeans-codebook`) | SSIM ≥ 0.99 vs splat-transform chunk |

### Phase D — Renderer & sort (Phase 2b, koşullu)

| ID | İş | Gate |
|----|-----|------|
| D1 | Tek global radix sort (unified pool) — N octree sort kapat | Profiling: sort > 15% frame |
| D2 | WebGPU path araştırması (URP dışı) | Yalnızca Web build hedefi varsa |
| D3 | `environment` tier geri ekle (exterior sahneler) | Kullanıcı exterior orbit istiyorsa |

### Phase E — Doğrulama

| Test | Metod |
|------|--------|
| Manifest parity | `counts[0] == sourceSplatCount` |
| Visual A/B | Aynı kamera: SuperSplat viewer vs Unity, SSIM / side-by-side |
| Progressive | Play R reset → 500ms coarse full scene → 3s içinde near-field L0 |
| Budget | Orbit + interior: `poolLod0` > 0, resident ≤ budget |
| Regression | Streak/needle: runtime `ClampScaleAnisotropy` maxAspect 6–8 |

---

## 6. Önerilen uygulama sırası (kullanıcı “sonra uygulayalım”)

```
1. Phase A (A1→A6)     ← LOD0’a ulaşamama + budget hissi; en acil
2. Phase B (B1→B3)     ← “instant complete coarse image” SuperSplat hissi
3. Phase C (C1 veya C2)← Bake kalitesi / LOD piramidi parity
4. Phase E             ← Her phase sonrası
5. Phase D             ← Profiling gate
```

**İlk PR önerisi:** “SOG runtime SuperSplat scheduler parity (A1–A4)” — tek commit, prefab retune, HUD doğrulama.

**İkinci PR:** “Coarse bootstrap (B1–B2)”.

**Üçüncü PR:** “Bake: splat-transform wrapper veya global decimate (C2 veya C1)”.

---

## 7. Bilinçli sapmalar (değiştirmeyebiliriz)

| Sapma | Gerekçe |
|-------|---------|
| Unity URP vs WebGPU | Unity hedefi; PlayCanvas engine port etmek scope dışı |
| Addressables vs HTTP CDN | Desktop/XR local StreamingAssets yeterli |
| shN shared VQ tek atlas | RAM/VRAM; SuperSplat chunk başına SOG — biz trade-off yaptık |
| Interior’da env kapalı | Festsaal interior; env whole-scene haze yapıyordu |
| Import-time aspect clamp | Streak fix; SuperSplat decimate ile farklı floater stratejisi |

---

## 8. İlgili repo dosyaları

| Konu | Path |
|------|------|
| SOG streamer | `package/Runtime/StreamedSog/SogStreamer.cs` |
| SOG update loop | `projects/.../GaussianLodStreamAsync.cs` → `UpdateSog()` |
| Budget balancer | `package/Runtime/Streaming/SogBudgetBalancer.cs` |
| Scheduler | `package/Runtime/Streaming/GsplatLodScheduler.cs` |
| Offline bake | `tools/gsplat_lod/sog_baker.py` |
| Rebake script | `tools/gsplat_lod/rebake_festsaal_200k_sog.sh` |
| Eski parity plan | `docs/SUPERSPLAT_PARITY_PLAN.md` |
| Mimari tasarım | `projects/GaussianExample-URP/STREAMED_LOD_DESIGN.md` |

---

## 9. Sonuç cevabı (kullanıcı sorusu)

**SuperSplat büyük ölçüde aynı yöntemi mi kullanıyor?**  
**Evet, mimari olarak aynı:** Streamed SOG + kd-tree + leaf başına bağımsız LOD + mesafe LOD + global budget + coarse-first progressive + never-evict swap.

**Farklı mı?**  
**Evet, önemli farklar var:**
- Offline: global decimate-then-chunk vs chunk-then-voxel-merge
- Runtime: bizde SOG path’te **global budget balancer ve underfill wired değil**
- Tuning: budget ve lodMultiplier SuperSplat’tan **çok daha muhafazakâr**
- İlk kare: SuperSplat **tüm sahneyi coarse ile doldurur**; biz **frustum subset**

Sonraki adım: **Phase A** ile runtime parity — bake (C) ikinci dalga.
