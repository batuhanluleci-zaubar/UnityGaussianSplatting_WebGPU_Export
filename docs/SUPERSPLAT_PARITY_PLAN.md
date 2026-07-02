# SuperSplat Streaming Parity — Plan & Action Report

Son güncelleme: 2026-07-02
Branch: `fix/spz-v3-and-large-splat-import`
Yazar: Claude oturum notu — background workflow `wpqhza523` bitince Appendix A güncellenecek

---

## 1. Yönetici Özeti

Kullanıcı ağrısı (dogrudan alıntı): _"playcanvas super splat her seyi duzgun yapmis ama bizimkiinde sikintilar var … butun spz'ler yuklenmiyor bir sikinti var"_ — hedef test yüzeyi **macOS Standalone**.

Kısa teşhis:
- **Format hedefi yanlış hizalanmış**: PlayCanvas SuperSplat, kullanıcı belgelerinden SPZ'yi çıkardı; kanonik streaming formatı artık **Streamed SOG** (kd-tree manifest + WebP-encoded unbundled chunks). Bizim mimari SPZ tek-blob delivery'e dayalı — SuperSplat streaming spec'i ile _protokol düzeyinde_ uyumsuz.
- **Manifest & load-ordering eksik**: SuperSplat `lod-meta.json` üzerinden per-chunk AABB + LOD tree + `filenames[]` indirection sağlıyor; bizde Addressables label seti var, deterministic dependency graph yok.
- **Coarse-first pin garantisi yok**: SuperSplat bir leaf'in coarse LOD'unu fine gelene kadar evict etmez ("no chunk hole"). ChunkBudgetCuller'da bu garanti henüz gerçekleştirilmedi — "eksik chunk" görüntüsünün 1. sıradaki adayı.
- **Sort optimizasyon kampanyası bitti**: 8.5ms → 7.62ms cumulative, CSV-verified. Ceiling yakın (~1.5-2ms daha CPU-side headroom). Sıradaki tek büyük perf lever = Aura Vulkan GpuSorting testi.

Bu belge üç iş hattını (**Track A → D**) sürüm sürüm parçalıyor. A'yı **hemen** başlatıyoruz (workflow arka planda); B ve C **onay gerektiren** stratejik kararlar; D perf follow-through.

---

## 2. Şu An Nerede Duruyoruz?

### 2.1 Shipped & CSV-verified

| İtem | Commit | Sonuç | Doğrulama |
|---|---|---|---|
| P0 area-weighted merge + behind-cam LOD penalty | `945b1c5` + `87370b5` | Coarse LOD "washed-out blob" gitti | `P0B_REBAKE_RESULTS.md` numerik doğrulama |
| P1(a) GpuSorting shader + C# scaffolding | `6b75bc9` | Metal kernel-fail (subgroup builtins), Vulkan **untested** | On-device gerekli |
| P1(b) Single-Pass Instanced stereo | `78adae3` | Mono regression-free | Editor A/B geçti |
| P2 chunk-centroid pre-sort | `5ab4532` | Basic version aktif | Global cross-chunk sort deferred |
| Slice 1 sort mikro-opt | `03e10bd` | 8.50 → 8.20 ms (−0.30) | CSV |
| Slice 2 LockBufferForWrite + guard'lar + frustum cache + CountBitsWGE16 | `5188454` + `0a5f590` + `599b460` + `3614bda` | 8.20 → **7.62 ms** (−0.58) | CSV (`append_upload` 1.0 → 0.023 ms) |
| Slice 3 strided-permutation LRU cache | `b718bc4` civarı | Shipped **OFF by default** — scene mismatch (`leafSize=1`) | `slice3_near_cache_on.png` pixel-identical |

**Cumulative sort delta**: −0.88 ms (−10.4 %) — CSV-backed, quotable.

### 2.2 Açık Kalan Konular

- ❌ **"Butun SPZ'ler yuklenmiyor"** — root cause henüz doğrulanmadı (workflow `wpqhza523` fazi çalışıyor). Bu belgenin **1 numaralı** kapatılacak boşluğu.
- ❌ **macOS Standalone build hiç test edilmedi** bu branch'te. Editor'de gördüğümüzün Standalone'da da geçerli olduğuna dair kanıt yok.
- ❌ **GpuSorting Aura üzerinde untested** — 30 dakikalık kesin bir test ile ~2-4 ms sort win potansiyeli.
- ❌ **Slice 3 gerçek scene'de doğrulanmadı** — Station4-şekilli scene (`leafSize ≥ 200`) gerekli.
- ❌ **DYNGSPLAT_ARASTIRMA_TR.md içinden 4D codebook-VQ (.gdelt v2)** — DynGsplat parity için son eksik parça (memory notu).

---

## 3. PlayCanvas SuperSplat Protokolü (özetle)

Full spec için: hub taraması Section 3-9 (bu oturumun agent raporu — Appendix B).

### 3.1 Format Ailesi

| Format | Rol | Compression | Kullanım |
|---|---|---|---|
| **PLY** | Source & interchange | Yok (lossless) | Training, editing, archival |
| **SOG** | Runtime & delivery (single-file) | 15-20× smaller than PLY (lossy) | Web app, CDN |
| **Streamed SOG** | Large-scene streaming | Same as SOG per chunk | Progressive load, HTTP range |

**SPZ user-manual'dan tamamen çıkarıldı.** `formats/streamed-sog/` kanonik streaming spec.

### 3.2 Streamed SOG Delivery

```jsonc
// lod-meta.json (root file)
{
  "version": 1,
  "count": <toplam gaussian>,        // env hariç
  "counts": [<lod0>, <lod1>, ...],   // her LOD'daki toplam sayı
  "lodLevels": <int>,
  "environment": "env/",             // opsiyonel backdrop
  "filenames": ["0_0/meta.json", "0_1/meta.json", ...],  // MUTLAKA bu indirection üzerinden
  "tree": {                          // binary spatial partition (kd-tree tarzı)
    "bound": {"min":[x,y,z],"max":[x,y,z]},
    "children": [<Node>, <Node>],     // interior tam 2 çocuk
    "lods": {                         // leaf-only
      "0": {"file": <filenames idx>, "offset": <int>, "count": <int>},
      "1": {...}, ...
    }
  }
}
```

Kritik kurallar (verbatim):
- **Binary spatial tree** — interior nodes _exactly_ two children.
- **LOD 0 = highest detail**; sonrakiler coarser.
- **All LOD levels of a leaf cover the SAME spatial region.**
- **Leaf `bound` encloses each Gaussian's rotated-scaled ellipsoid extent — NOT centers.**
- **Path inference forbidden**: `{lod}_{chunk}/` naming sadece writer convention'ı; readers MUTLAKA `filenames[]` üzerinden resolve etmeli.
- **Precision**: `lod-meta.json` sayıları 7 sig-fig'e quantize (float32).

### 3.3 Per-chunk SOG Payload

Her chunk = **unbundled** SOG dir (bundled `.sog` zip değil; HTTP range için parçalı):
- `meta.json` — versioning + per-axis min/max
- `means_l.webp`, `means_u.webp` — position 16-bit split (upper<<8 | lower → symmetric log-domain lerp → exp)
- `scales.webp` — RGB triple → 256-entry log codebook → exp
- `quats.webp` — smallest-three quaternion (3×8-bit + 2-bit mode, 26 bits total)
- `sh0.webp` — base color + opacity (DC = 0.5 + codebook[i]*SH_C0, alpha ham [0,255])
- `shN_centroids.webp` + `shN_labels.webp` — VQ SH (16-bit label, up to 65 536 centroids, 64 px/row packed)

### 3.4 Runtime Davranışı

- **LOD selection**: `lodBaseDistance * lodMultiplier^i`, **default mult = 3, min clamp 1.2**.
- **Load ordering**: coarse-first. Bir leaf'in coarse LOD'u fine gelene kadar **evict edilmez** — chunk hole olmasın diye.
- **Sort**: engine tüm resident splats için **tek global sort** (per camera/layer) → work buffer → draw. Hiyerarşik / per-chunk sort yok.
- **Streaming ↔ rendering decoupled**: streaming loop her frame çalışır (render çizmese bile).
- **Events**: `frame:request` (yeni chunk data hazır → render iste), `frame:ready` (scene tam yüklendi + sıralandı + çizildi).
- **Shader hooks**: `gsplatModifyVS` + `gsplatModifyPS`, hem GLSL (WebGL) hem WGSL (WebGPU) chunk'ları paralel register.

### 3.5 "All chunks must load" garantisi

**YOK.** Streamed SOG explicit olarak on-demand tasarlandı. Fallback tamamen coarse-LOD-pinning'e bağlı: bir leaf'in _herhangi_ bir LOD'u resident'sa boşluk görünmez. Bizim kritik gap.

---

## 4. Bizim Mimari — Neresi Uyumsuz

### 4.1 Format
- **Delivery**: SPZ v3 single-blob (Niantic). Baker: `tools/gsplat_lod/chunk_lod.py` + `merge_lod.py` (area-weighted moment merge).
- **İki kopya senkronize kalmalı**: `Assets/StreamingAssets/` + `Assets/GaussianAssets/` (memory notu `gsplatxr-sh-quality.md`).

### 4.2 Delivery
- **Addressables 3.1.0** — label-based lookup. `lod-meta.json`-eşdeğeri manifest **yok**.
- Per-chunk AABB, LOD-tier `{file, offset, count}` slice indirection, `filenames[]` deref yok.

### 4.3 Runtime
- `projects/GaussianExample-URP/Assets/Scripts/Cinematic/GaussianLodStreamAsync.cs` streaming controller
- `ChunkBudgetCuller.cs` budget + eviction (cooldown var, coarse-pin garantisi YOK)
- LOD formula **uyumlu** (`lodBaseDistance * lodMultiplier^i`) ama `lodMultiplier` default'umuz ~1.5, SuperSplat 3
- Sort: P2'de chunk-centroid pre-sort var — SuperSplat "single global sort" ile _yakın_ ama identik değil
- Ellipsoid-extent bound: culler_ımız merkeze göre olabilir (audit fazi bunu doğrulayacak)

---

## 5. Gap Matrix

| # | Boşluk | Severity | Tespit | Uygulama Efor | Track |
|---|---|---|---|---|---|
| G1 | Deterministic load ordering (manifest yok) | **HIGH** | "chunks not loading" — muhtemel #1 | Baker JSON + reader | B |
| G2 | Coarse-LOD pin garantisi eksik | **HIGH** | Chunk hole ihtimali | Culler değişikliği | A |
| G3 | `filenames[]` indirection eksik | **HIGH** | Sessiz chunk atlama | Address pattern + reader | A/B |
| G4 | Ellipsoid-extent bound (center değil) | MED | Boundary chunks atlanabilir | Culler AABB genişlet | B |
| G5 | `lodMultiplier` default farklı (1.5 vs 3) | MED | Fazla concurrent LOD → mem baskısı → cull → hole | Config tune | A |
| G6 | Bundled vs unbundled chunk | LOW | SPZ single-blob; HTTP range yok, tam indir zorunda | Format değişikliği | C |
| G7 | Version fields + validate | MED | Sessiz mismatch = partial-load | Reader güard | B |
| G8 | Environment splats tier | LOW | Uzak range'te env evict olabilir | Ayrı residency queue | B |
| G9 | Single global sort (chunk-centroid yerine) | LOW | Popping possible, henüz görünür değil | Global radix pass | D (bkz. GpuSorting) |
| G10 | Streaming ↔ rendering decoupling | LOW | Kamera hareketsiz kaldığında chunk yüklenmezse gecikme | Async pump kontrol | A |
| G11 | Shared SH codebook (per-asset, per-chunk değil) | LOW | VRAM waste → early eviction | Codebook shared alloc | C |
| G12 | Rendering fallback (loading placeholder) | LOW | Missing chunk = tam boşluk | Coarse-render always-on | A (G2 ile birlikte) |

Track kolonu: **A = immediate**, **B = SPZ-path parity**, **C = Streamed SOG reader**, **D = perf follow-through**.

---

## 6. Aksiyon Planı

### Track A — IMMEDIATE Bug Fix (1-2 gün, workflow in-progress)

**Amaç**: macOS Standalone build'de scene açılınca 20/20 chunks yüklensin, chunk hole yok. Format değişikliği yok.

| Adım | İş | Kim | Süre | Kanıt |
|---|---|---|---|---|
| A1 | Root cause diagnose (`wpqhza523` workflow bitince) | Claude/workflow | in-progress | Chunk state transition log + Player.log |
| A2 | Missing chunks kill (Addressables handle/label patch) | Claude | 2-4 saat | Editor + Standalone 20/20 |
| A3 | Coarse-LOD pin garantisi (`ChunkBudgetCuller`: visible leaf'in en coarse resident LOD'u ASLA evict) | Claude | 3-4 saat | Screenshot A/B; chunk hole yok |
| A4 | `lodMultiplier` SuperSplat default 3'e yükselt (min clamp 1.2) | Claude | 15 dakika | FPS + memory delta CSV |
| A5 | Streaming decoupling: kamera hareketsizken de pump devam etsin | Claude | 1 saat | Static-camera 60s log |
| A6 | macOS Standalone build + Player.log verify | User + Claude | 30 dakika | `Applications.log` grep temiz |

**Definition of Done**: Phase2HQ Standalone build, NEAR view 20/20 chunks; FAR view boşluk yok; log'da `Addressables Failed to load` yok.

### Track B — SPZ-Path SuperSplat Parity (3-5 gün, kullanıcı onayı gerektirir)

**Amaç**: Format'ı SPZ olarak tutup SuperSplat-benzeri robustluk kazan. Streamed SOG'e geçmiyoruz ama gap'leri SPZ üstüne örtüyoruz.

| Adım | İş | Süre |
|---|---|---|
| B1 | Baker'a `lod-meta.json` eşdeğeri JSON manifest üretimi ekle (`tools/gsplat_lod/chunk_lod.py`): per-chunk AABB + LOD tree + `filenames[]` | 1 gün |
| B2 | Runtime manifest reader (`GaussianLodStreamAsync.cs`): Addressables label yerine manifest'ten LOD tree walk | 1 gün |
| B3 | Ellipsoid-extent AABB culler (bound genişlet: her Gaussian'ın rotated-scaled ellipsoid'i) | 0.5 gün |
| B4 | Version fields + startup validation (mismatch → hard fail) | 0.5 gün |
| B5 | Environment splats tier (`env/` ayrı residency queue, evict edilmesin) | 0.5 gün |
| B6 | A/B test: Phase2HQ + Station4 üzerinde load determinism, no chunk hole, no popping | 1 gün |

**Definition of Done**: Aynı scene 10 farklı camera path'ında deterministic aynı chunk load sırası; no visual regression.

### Track C — Streamed SOG Reader (2-3 hafta, stratejik karar)

**Amaç**: Gerçek SuperSplat parity — kd-tree manifest + WebP-encoded unbundled SOG chunks yükleyebilen native Unity reader.

| Adım | İş | Süre |
|---|---|---|
| C1 | SOG spec impl: pozisyon 16-bit split decode, quat smallest-three, scale/sh0/shN VQ codebook | 5 gün |
| C2 | `lod-meta.json` kd-tree reader + traversal | 2 gün |
| C3 | WebP decode (Unity `ImageConversion` + custom path) | 2 gün |
| C4 | Runtime side-by-side reader factory: SPZ vs Streamed SOG toggle | 1 gün |
| C5 | SuperSplat Editor / SplatTransform CLI ile bake test | 2 gün |
| C6 | Perf comparison: SPZ vs SOG aynı scene | 2 gün |

**Karar noktası** (kullanıcı için): Track C'ye başlamadan _önce_ soru — bu proje SuperSplat _formatı_ ile uyumlu asset delivery mi istiyor (evet → C zorunlu), yoksa sadece SuperSplat _davranışı_ mı (hayır → B yeterli)?

### Track D — Perf Follow-through (paralel, sort optimize kampanyasının kalanı)

| Adım | İş | Süre | Beklenen |
|---|---|---|---|
| D1 | Aura Vulkan on-device GpuSorting test (30-min build+run, `cs.IsSupported()` + Dispatch) | 30 dakika + analiz | Confirms Vulkan çalışıyor mu → −2 to −4 ms sort Aura'da |
| D2 | Metal DeviceRadixSort graft (aras-p PR#82: WaveActiveBallot uint4 cast + simdgroup_barrier + shift guard) | 1-2 gün | −0.5 to −0.8 ms desktop Metal + Metal 2.0 subgroup builtins pre-existing hatasını temizler |
| D3 | Slice 4: collect (3.12 ms) + start (1.24 ms) mikro-opt | 1 gün | −0.5 to −1.0 ms CPU-side ceiling |
| D4 | Slice 3 cache Station4 scene'de doğrulama (leaf ≥ 200) | 0.5 gün | Confirms design projesyonu (yeni scene warmup) |

---

## 7. Milestone Chart

```
M-A (2 gün)  : Standalone 20/20 chunks + no hole                   [Track A]
                 │
                 ├─→ M-B (1 hafta)   : SPZ-path SuperSplat-benzeri  [Track B]
                 │                     robust, deterministic
                 │
                 ├─→ M-C (opsiyonel)  : Streamed SOG reader shipped [Track C, 3 hafta]
                 │                     (kullanıcı onayı gerekli)
                 │
                 └─→ M-D (paralel)    : Aura Vulkan 90+ FPS         [Track D]
                                        Metal sort −0.5 ms
```

**Kritik path**: Track A → M-A → sonrası kullanıcı kararı. Diğer tracklar A tamamlanana kadar spekülatif.

---

## 8. Test Protokolü — macOS Standalone

### 8.1 Build Setup (bir kere)
1. `File > Build Profiles > macOS Standalone`
2. `Player Settings > Player > Standalone > Other Settings`:
   - Graphics APIs: Metal only (auto-select off)
   - Scripting Backend: IL2CPP
   - Target Architecture: Apple Silicon (Universal isteğe bağlı)
3. `Window > Asset Management > Addressables > Groups > Build > New Build > Default Build Script` **her build'den önce**.

### 8.2 Instrumentation
- `RendererMarkerRecorder` üzerinde: `alsoWriteCsv = true`, `logIntervalSeconds = 2`.
- **Yeni gerekli** (A1 çıktısı): chunk state transition log (Requested → Loading → Loaded → Failed → Evicted, chunk-id ile).

### 8.3 Test Runs
| Run | Scene | Camera path | Süre | Kanıt |
|---|---|---|---|---|
| R1 | Phase2HQ | NEAR (~-1.3, 5, -15) sabit | 30 s | `gsplat_perf.csv` + chunk log |
| R2 | Phase2HQ | NEAR → FAR yavaş orbit | 60 s | Screenshot her 10 s + chunk log |
| R3 | Phase2HQ | Kamera arkaya döndür (behind-cam penalty) | 30 s | Budget CSV |
| R4 | Station4 | NEAR sabit | 30 s | Slice 3 cache warmup ölçümü |

### 8.4 Log Analiz
```bash
# Standalone Player log konumu
tail -f "~/Library/Logs/DefaultCompany/UnityGaussianSplatting/Player.log"

# Chunk hataları
grep -E "(Chunk|Addressable|SPZ|Failed|NullRef|AsyncOperationHandle)" Player.log

# CSV output
cat "~/Library/Application Support/DefaultCompany/UnityGaussianSplatting/gsplat_perf.csv"
```

### 8.5 Pass Criteria
- ✅ 0 `Failed to load` log
- ✅ 20/20 (Phase2HQ) chunks Loaded state'e ulaşmış — chunk log sayımından
- ✅ NEAR view screenshot'ta hiçbir belirgin boşluk yok
- ✅ FAR → NEAR orbit sırasında pop-in bir frame'den kısa
- ✅ sort_ms rolling avg < 8.0 ms (mevcut editor baseline; Standalone daha iyi olmalı)

---

## 9. Riskler ve Tradeoffs

### 9.1 Format Kararı (SPZ vs SOG)

| Konu | SPZ (mevcut) | Streamed SOG (SuperSplat) |
|---|---|---|
| Baker | `tools/gsplat_lod` mevcut | SuperSplat / SplatTransform CLI harici tool |
| Compression | ~15-30 % (SPZ) | 15-20× PLY (WebP + VQ) — çok daha iyi |
| Delivery | Tek blob per chunk | Dir per chunk (unbundled, HTTP range) |
| Runtime reader | Mevcut, çalışıyor | Yazılmadı (C1-C3, 2-3 hafta) |
| SuperSplat ekosistem uyumu | ❌ | ✅ SuperSplat Editor asset'leri direkt yüklenir |
| Aura I/O baskısı | Tek büyük blob → tepesi yüksek stream | Küçük parça → smooth I/O curve |

**Öneri**: Track A + B'yi bitir. Track C'yi ancak SuperSplat Editor ecosystem'inden asset almak zorunda kalırsak veya Aura'da SPZ I/O tepe noktası regresyona yol açarsa açalım.

### 9.2 Coarse-Pin Memory Ceiling

Coarse LOD'ların visible olduğunda evict edilmemesi memory tavanını artırır. Aura'nın VRAM'i ~2 GB pratik. Hesaplama:
- 20 chunk × 5 LOD × ~200 KB avg SPZ = ~20 MB per LOD level per chunk seti
- Coarse-pin sadece _visible_ leaf'ler için, evict cooldown _off-screen_ için hâlâ çalışıyor → net delta < 50 MB
- **Kabul edilebilir** — riskin farkında olarak proceed.

### 9.3 `lodMultiplier` 1.5 → 3.0 Değişimi

Daha büyük multiplier ⇒ LOD bandları daha ayrık ⇒ concurrent-resident LOD sayısı düşer ⇒ memory rahat. Ama:
- Daha ani pop-in olabilir (LOD geçişleri daha büyük mesafe farkında)
- Auto-tune mantığımız (memory `gsplatxr-lod-budget.md`: "device targetFrameMs MUST be measured") 3.0'ı override edebilir

Öneri: Track A5 sonrası 3.0 dene, pop-in gözlemlenirse 2.0'a çek.

### 9.4 Manifest Eklenmesi (Track B1-B2)

Baker + reader iki yerde değişiklik gerektiriyor. Version bump kaçınılmaz. Mevcut asset'ler için:
- Backward compat: version yoksa → `lodMultiplier` default 3, coarse-pin off (eski davranış) fallback
- Yeni asset'ler için manifest zorunlu

---

## 10. Kapsam Dışı (Do NOT do)

Aşağıdakiler workflow raporlarıyla veya önceki oturum kararlarıyla **elendi** — bir daha çıkarmıyoruz:

| İtem | Neden |
|---|---|
| FidelityFX ParallelSort port | Aynı Metal wave-op sorunu + DeviceRadixSort'tan yavaş (aras-p ölçümü 2.4 → 1.1 ms) |
| HDRP RadixSort extraction | Public API değil; light-clustering'e tightly coupled |
| GPU bitonic sort | 2M keys @ Aura tahminen 20-40 ms → framerate katliamı |
| P3 unified buffer refactor | Submit-alone 0.1 ms ölçüldü, tavan kazancı 0.1 ms |
| P4 file packing / SOG WebP (bu belge dışı) | Workflow'da "do NOT do" — Track C dışında ayrıca ele alınmayacak |
| macOS Metal desktop WaveIntrinsic hack (MSL patch) | Unity toolchain HLSL→DXC→SPIRV→SPIRV-Cross kapalı; aras-p PR#82 doğru yol (Track D2) |

---

## 11. Sıradaki Somut Adım

### 11.1 User Commitment (2026-07-02)

Kullanıcı doğrudan alıntı: _"hepsine sirayla odaklan"_ → **A → B → C → D sıralı çalışılacak**. Karar noktası yok, her track sırasıyla ele alınacak; C ertelenmeyecek.

### 11.2 Execution Sequence

```
[NOW]  Track A (workflow wpqhza523 in-progress)  ┐
       Track C recon (parallel research)          ┘  ── background
              │
              ↓ (workflow A biter bitmez)
       Track A verify + close-out (Standalone build test)
              │
              ↓
       Track B (SPZ-path parity, 3-5 gün)         ← workflow B kick-off
              │
              ↓
       Track C (Streamed SOG reader, 2-3 hafta)   ← workflow C kick-off (recon zaten hazır)
              │
              ↓
       Track D (perf follow-through)              ← workflow D kick-off
              │
              ↓
       [DONE] SuperSplat parity + Aura target + macOS Standalone verified
```

### 11.3 Şu An Yürütülüyor

| İtem | Task ID | Durum |
|---|---|---|
| Track A workflow (audit + diagnose + implement + macOS Standalone build) | `wpqhza523` | 🟡 running |
| Track C recon (SOG WebP decode, Unity image APIs, kd-tree traversal patterns) | (spawn edilecek) | 🟡 running |

### 11.4 Milestone Deadlines (best-effort)

| Milestone | ETA | Definition of Done |
|---|---|---|
| M-A | +2 gün | Standalone 20/20 chunks; 0 log hatası; commit'ler pushed |
| M-B | +1 hafta (A sonrası) | JSON manifest baker + reader + ellipsoid AABB culler shipped; deterministic load order |
| M-C | +3 hafta (B sonrası) | Streamed SOG reader; SuperSplat editor asset direkt yüklenebilir; SPZ ↔ SOG side-by-side toggle |
| M-D | paralel + M-C sonrası | Aura Vulkan sort ~2-4 ms; Metal desktop ~0.5 ms; Slice 4 CPU ceiling |
| **Total** | ~5-6 hafta | Full SuperSplat parity + Aura hedef FPS + macOS Standalone verified |

---

## Appendix A — Track A Workflow Sonuçları (2026-07-02, `wpqhza523`)

### A.1 Uygulanan Fixler (commit `112081a`, push edildi)

| ID | İş | Dosya | Durum |
|---|---|---|---|
| A1 | StreamingAssets manifest resolver (relative-path first probe) | `GaussianLodStreamAsync.cs` + `GaussianLodStreamer.cs` + `StreamingAssets/gsplat_lod/uhq/manifest.json` | ✅ |
| A2 | Addressables auto-build flag açık + StandaloneOSX bundle | `AddressableAssetSettings.asset` (workspace-only, gitignored) | ⚠️ commit'e girmedi |
| A3 | Fail-loud + strand-slot fix (`AsyncOperationStatus.Failed` → `Debug.LogError` + slot clear + retry) | `GaussianLodStreamAsync.PollLoads` | ✅ |
| B1 (bonus) | `lodUnderfillLimit=3` + `SelectUnderfillLevel_Diag()` — never-drop-visible tolerance | `GaussianLodStreamAsync.cs` | ✅ |
| B2 (bonus) | `stagedPrefetch=true` — coarse-first initial load target | `GaussianLodStreamAsync.Evaluate` | ✅ |
| B3 (bonus) | Refcount + cooldown eviction (`cooldownFrames=100` ~1.4s @72Hz) — no bundle re-read on rapid re-request | `GaussianLodStreamAsync` + `m_Cooldown/CoolEntry` | ✅ (initial `Collection was modified` crash → fixed with `m_CooldownKeys` snapshot) |

**Not**: Workflow, plan Track A tarifinin ötesine geçip Track B'nin ilk 3 maddesini (B1-B3 diyerek) de shipledi. Bu üçü aslında **plan Track B**'nin item'ları değil (planımdaki B = baker manifest + reader + ellipsoid AABB + version + env tier). Workflow, "SPZ'ler yüklenmiyor" bug'ının robust çözümü için organik olarak bu 3 runtime davranışını da ekledi — kabul edilmesi gereken güzel bonus.

### A.2 Editor Play Doğrulama

- **64/64 chunks resident** (Phase2HQ, kamera geri çekilmiş synthetic test: Cinemachine + AutoFramer + TourRig disabled)
- `hasCur=64, hasPen=0, slotted=64, cooldown=0, m_ResidentChunks=64, m_VisibleChunks=64`
- Console errors = **0**
- Log'da `[StreamAsync] manifest resolved via StreamingAssets` confirmed
- **Caveat**: Cinemachine-driven normal playback'te ilk pose ~20 chunk visible, kalan 44 coarsest-only kalır — bu **doğru davranış** (never-drop-visible + staged prefetch), bug değil

### A.3 macOS Standalone Build

| Deneme | Sonuç |
|---|---|
| 1. build (target=osx, output=`/tmp/gsplat_build/Gsplat.app`) | ✅ SUCCEEDED — 790 MB, manifest + `aa/StandaloneOSX/*.bundle` doğru shipped. Ama YANLIŞ SCENE (`GSTestScene`, Phase2HQ değil çünkü `EditorBuildSettings`'e eklenmemişti). App başlatıldı: Player.log boot OK, `XrSceneBootstrap` fires, `[StreamAsync]` yok (o scene'de streamer yok). |
| Phase2HQ scene ekle | `manage_build action=scenes` ile eklendi → `EditorBuildSettings.asset` modified (**uncommitted**) |
| 2. build (Phase2HQ dahil) | ❌ FAILED — **10× IOException: No space left on device** on `Library/Bee/artifacts/*.mvfrm`. Root disk 98% dolu, ~259 MiB free. Task rule per user prompt: "if MCP build errors out, capture verbatim and stop — do NOT force it." Agent doğru şekilde durdu. |

### A.4 Uncommitted / Ignored Değişiklikler

Bu değişiklikler workspace'te ama commit'te değil — user aksiyon:

1. **`AddressableAssetSettings.asset`** (`AddressableAssetsData/` gitignored) — flag değişikliği başka dev fresh checkout'ta kaybolacak. `.gitignore`'dan çıkarmayı düşün.
2. **`ProjectSettings/EditorBuildSettings.asset`** (Phase2HQ scene eklendi) — modified, uncommitted. Bir sonraki commit'e dahil edilmeli.
3. **`Assets/Phase2HQ.unity`** (manifestPath absolute → relative değişikliği) — save edildi ama diff'te görünmüyor olabilir; `git status` ile teyit et.

### A.5 Kullanıcı Aksiyonu Gerekli (M-A close-out)

1. **Disk temizle**: `~/Library/Caches`, Xcode DerivedData, eski Unity `Library/Bee` cache'leri — birkaç GB gerekli
2. **Rebuild**: `manage_build action=build target=osx output_path=/tmp/gsplat_build/Gsplat.app options=["clean_build"]` — Phase2HQ artık build settings'de
3. **Verify**: `.app` başlat, `~/Library/Logs/DefaultCompany/*/Player.log` grep:
   - ✅ `[StreamAsync] manifest resolved via StreamingAssets` içeriyor
   - ✅ `load FAILED` yok
   - ✅ HUD'da `m_ResidentChunks` 6s içinde 64/64 (kamera settle sonrası)
4. Uncommitted 3 dosyayı gözden geçir + commit et (§ A.4)

### A.6 Bu Kapatmadan Track B'ye Geçmiyorum

Track B (baker JSON manifest + ellipsoid AABB culler + version fields + env tier) Standalone build doğrulanana kadar başlamayacak. Aksi halde bug fix'lerin gerçek device'da çalıştığını bilmeden parity work'e girmiş oluruz.

## Appendix B — SuperSplat Protokol Detayları

Ayrıntılı `lod-meta.json` schema, SOG per-attribute decode formülleri, engine event flow için: bu oturumun hub-recon agent raporu (Section 3-9). Gerekirse bu belgenin bir alt-dokümanına extract edilebilir (`docs/SUPERSPLAT_STREAMED_SOG_SPEC.md`).

## Appendix D — Track C Blueprint (2026-07-02, `wg0tg1tyo` bitti)

Recon workflow'u Streamed SOG reader implementation blueprint'ini teslim etti. Özet:

### D.1 Boyut & Efor
- **~1140 yeni LOC + 240 diff LOC**
- **16 takvim günü** (C1-C6 alt fazları)
- Runtime location: `package/Runtime/StreamedSog/` (Editor değil, Aura Vulkan ship için)

### D.2 Prerequisite Blocker
- **`InputSplatData` struct'ı `GaussianSplatting.Editor.Utils` → `GaussianSplatting.Runtime` namespace'e taşınmalı** (~2 saat).
- **Track B3 (ellipsoid-extent AABB culler) shipped olmadan Track C başlamaz** — SOG scenes boundary chunk gap gösterir.

### D.3 Eklenmesi Gereken Dosyalar (`package/Runtime/StreamedSog/`)

| Dosya | LOC | Amaç |
|---|---|---|
| `SogLodMeta.cs` | ~140 | `lod-meta.json` POCO + JObject-walk parser (version==1 + interior-children==2 validate) |
| `SogChunkMeta.cs` | ~110 | Per-chunk meta.json POCO (nullable-float codebook slots for null-patch) |
| `SogKdTree.cs` | ~200 | Recursive-descent leaf extractor + zero-alloc traversal; `NativeArray<int>` stack, ellipsoid-extent AABB test |
| `SogLeafNode.cs` | ~60 | Runtime leaf struct, 32B aligned SoA/Burst |
| `SogChunkLoader.cs` | ~250 | Ref-counted async chunk loader + 100-frame cooldown |
| `SogChunkResource.cs` | ~90 | Decoded RGBA per attribute (meansL/U, scales, quats, sh0, shN centroids/labels) |
| `SogDecoder.cs` | ~180 | BurstCompile IJobParallelFor jobs per attribute |
| `SogCodebooks.cs` | ~100 | Reusable Burst helpers (PatchNullCodebook, InvLogTransform, UnpackSmallestThreeQuat, SigmoidInvOpacity) |
| `SogReader.cs` | ~120 | Public entry: `LoadManifest(path)` + `ReadLeafLod(...)` |
| `SogStreamer.cs` | ~280 | Per-frame state machine (selectDesiredLodIndex + prefetchNextLod + pendingDecrements) |
| `SogBudgetBalancer.cs` | ~160 | Sqrt-bucket global budget balancer (deferred plan Track A item) |

### D.4 Kritik Karar Noktaları

- **Kd-tree runtime traversal YOK**: PlayCanvas'ta tree build-time artifact. Load'da flat `LeafNode[]` array'e düzleştir, runtime iterasyon flat üstünde.
- **WebP**: `netpyoung/unity.webp` (OpenUPM, `com.netpyoung.webp@0.3.22`) — libwebp binding. macOS `.dylib` + Android arm64/armv7/x86_64 `.so` ships. Zero managed copies: `WebPDecodeRGBAInto` direkt `NativeArray<byte>`.
- **JSON**: Newtonsoft.Json 3.2.2 (transitive → explicit direct dep). JsonUtility hitap etmez (Dictionary + discriminated union).
- **Per-attr decode formulas verbatim** (blueprint'te tam liste; kısa hatırlatma):
  - `SH_C0 = 0.28209479177387814` (hardcode)
  - Quat: norm=√2, mode tag byte-252, invalid → identity
  - Position: `n = ((hi<<8)|lo) / 65535` (CPU parity, 257 değil), `world = sign(lerp(mins,maxs,n)) * (exp(|lerp|) - 1)`
  - Scale: `exp(codebook[byte])`
  - SH0 DC: `r,g,b = 0.5 + codebook[byte] * SH_C0`, alpha=`byte/255` (V2 sigmoided; SPZ v3 logit expected → `sigmoidInv` decode)
  - ShN: label 16-bit, `u = (label%64) * shCoeffs`, `v = label/64`, `shCoeffs = {1:3, 2:8, 3:15}[bands]`, texture width MUST = `64*shCoeffs`
- **SuperSplat null-codebook workaround**: `codebook[0]==null` ise `codebook[0] = codebook[1] + (codebook[1]-codebook[255])/255`. Pre-2025 SuperSplat asset'leri için ZORUNLU.

### D.5 Perf Budget

- Aura pure-C# + native libwebp: ~15-40 ms per 50k-splat leaf → 20 chunk × 5 LOD worst = ~1.5 s
- **500ms hedef sadece LAZY parse ile**: sync olarak sadece visible-LOD-per-leaf (~20 decode) at load, geri kalanı streaming pump'a defer
- Chunk-per-renderer pool `GaussianLodStreamAsync.cs`'te ZATEN var; bir SOG leaf → bir pool slot → bir renderer + per-LOD asset swap. Interior kd-tree node RENDERER ALMAZ.

### D.6 Data Flow

```
.sog dir on disk
  → lod-meta.json JObject parse
    → flatten tree → LeafNode[] + resolve filenames[] paths
      → per-frame SogKdTree.WalkVisibleLeaves → LeafCandidate[]
        → SogChunkLoader.EnsureResident (async WebP decode via libwebp → NativeArray)
          → SogDecoder Burst jobs (RGBA → InputSplatData)
            → GaussianSplatAsset populate
              → existing renderer + culler + sorter (UNTOUCHED)
```

### D.7 Risk Register (top 5)

1. **Android libwebp linking** — arm64 `.so` prebuilt ama IL2CPP stripping ayarları yanlış olursa runtime symbol not found. Test protokolü: erken bir stub decode + `adb logcat` grep.
2. **Kd-tree memory blowup** — 100+ leaf × 5 LOD × pooled chunk resource ~200 MB VRAM. Cooldown map + refcount doğru işlemeli.
3. **VQ SH memory** — 65 536 centroid × 3 bands × 15 coefs × 4 B = ~11.7 MB per chunk. Per-asset shared codebook ZORUNLU (per-chunk allocation'a düşerse VRAM patlar).
4. **meta.json version drift** — SuperSplat V1 vs V2 vs future. Startup hard-fail + user-visible error mesajı.
5. **Per-attr decode perf** — Burst compile settings yanlış olursa 40 ms → 200 ms drift. Burst inspector doğrulanmalı.

### D.8 Success Criteria

- SuperSplat editor'de üretilen bir `.sog` asset'i (5+ chunks, 3+ LOD) Unity'ye direkt yüklenir
- SPZ ↔ SOG side-by-side toggle: aynı scene, aynı camera, visual regression ≤ 1% (SSIM)
- Aura Vulkan build: 20 chunks NEAR view LOD-band correct, sub-60ms load time cold cache
- Player.log'da hiçbir `libwebp not found` / `codebook null` / `version mismatch` yok

### D.9 Track C Fazları

| Faz | İş | Süre |
|---|---|---|
| C0 | `InputSplatData` runtime namespace refactor | 2 saat |
| C1 | Newtonsoft.Json + libwebp OpenUPM add + manifest parser | 2 gün |
| C2 | Per-attr Burst decoder (means/quats/scales/sh0/shN) + null-codebook patch | 3 gün |
| C3 | KdTree flatten + streamer state machine + chunk loader refcount | 3 gün |
| C4 | GaussianLodStreamAsync entegre + SPZ↔SOG factory pattern | 2 gün |
| C5 | SuperSplat CLI'de üretilen asset ile side-by-side test | 2 gün |
| C6 | Aura Vulkan on-device build + logcat verify | 1 gün |
| **Toplam** | | **~16 gün** |

## Appendix C — Perf Baseline Snapshot (Slice 3 sonrası)

| Sub-marker | Değer | Notlar |
|---|---|---|
| `sort_total` | **7.62 ms** | 120-frame rolling CSV, Phase2HQ NEAR |
| `collect_ms` | 3.12 ms | BFS + Sort + frustum planes |
| `start_ms` | 1.24 ms | Task.Run spawn overhead × 20 chunk |
| `wait_ms` | 0.0005 ms | Join dead, per-node sort invisible |
| `append_cpu_ms` | 3.20 ms | Strided per-index copy (Slice 3 hedefi, Phase2HQ'da vurmadı) |
| `append_upload_ms` | 0.023 ms | ← Slice 2 win (was ~1.0 ms) |

**Ceiling**: CPU-side toplam kalan headroom ~1.5-2.0 ms. Daha fazlası için Track D1 (Aura Vulkan).
