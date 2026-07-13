# v1.0.0 - 環境遮蔽 for YMM4

YukkuriMovieMaker4 向けの環境遮蔽エフェクトプラグインの初回リリースです。
Direct2D カスタムピクセルシェーダーが 2 段階で処理し、前半で塗り分けの模様と形状の陰影を多スケールで見分け、後半で映像の明るさを起伏とみなして複数方向へ地平線を走査し、くぼみに陰を落とします。
奥行き情報や 3D モデルを使わず、単一フレームの明るさからスクリーン空間で遮蔽を推定します。
8 言語リソース構成の UI を備えます。

---

## 新機能

### 1. ピクセルシェーダー（2 パス構成）

`AmbientOcclusionPhase.hlsl` の位相パスと `AmbientOcclusion.hlsl` の本体パスの 2 段階で処理します。追加テクスチャは使用せず、いずれも入力映像の輝度を Rec.709 係数 `(0.2126, 0.7152, 0.0722)` で求めます。

#### 位相パス（模様の判別）

`AmbientOcclusionPhase.hlsl` の `main` は、明暗の変化が塗り分けの模様によるものか形状の陰影によるものかを判別するマスクを作ります。`ScaleResponse` はスケール 1・2・4px で輝度の 1 次差分と 2 次差分を求め、スケールで正規化した応答を返します。細スケールに局在する応答は模様、スケール間で一致する応答は形状として扱います。

| 値 | 説明 |
|---|---|
| `fine` | スケール 1・2px の応答の平均 |
| `coarse` | スケール 4px の応答 |
| `localization` | `saturate(2 × (fine − coarse) / (fine + coarse))` の局在度 |
| `energyGate` | 微小な変化を無視するためのゲート |
| `albedoEdge` | `localization × energyGate × sensitivity` の模様らしさ |

出力は `float4((1 − albedoEdge) × a, albedoEdge × a, 0, a)` で、R チャンネルに本体パスが使うゲート値をプリマルチプライドで格納します。`sensitivity` は 1 で固定です。

#### 本体パス（地平線走査）

`AmbientOcclusion.hlsl` の `main` は、入力映像（`t0`）と位相パスの出力（`t1`）を受け取ります。`strength <= 0` または `source.a <= 0` のときはソースをそのまま返します。輝度を高さとみなし、`directionCount`（2〜16）方向へ `stepCount`（1〜12）回、半径 `radiusPx` まで走査します。各ステップで高さ差 `seg = lum − prevLum` を、位相パスのゲート値 `w` で `lerp(1, wSeg, suppression)` の重みを掛けて累積します。累積した高さから傾き `hRel × (heightGain × 40) / t` を求め、その最大値を地平線とします。

| 値 | 説明 |
|---|---|
| `dirs` / `steps` | 走査する方向数とサンプル数 |
| `w` | 位相パスの R から読むゲート値（模様ほど小さい） |
| `suppression` | ゲート値を混ぜる割合。0 で生の高さ差 |
| `horizon` | 方向ごとの地平線の傾き |

方向ごとに `occ = horizon / (1 + horizon)` を求め、平均に 1.5 を掛けて `ao` とします。陰は `shade = lerp(白, shadowColor, ao × strength)` で、出力色は `source.rgb × shade` です。高さは 8bit の中間テクスチャの量子化による縞を避けるため元映像から直接読み、位相マップからはゲート値だけを読みます。出力はプリマルチプライドを保ちます。

---

### 2. カスタムシェーダーエフェクト

処理は `AmbientOcclusionPhaseCustomEffect` と `AmbientOcclusionCustomEffect` の 2 つで構成します。

`AmbientOcclusionPhaseCustomEffect` は `[CustomEffect(1)]` の 1 入力エフェクトです。

| プロパティ | 型 | 範囲 |
|---|---|---|
| `Sensitivity` | `float` | 0〜4 |

`MapOutputRectToInputRects` は近傍サンプルに必要な 5px 分だけ入力矩形を拡張します。定数バッファーは `Sensitivity` と 3 つの詰め物の 16 バイトです。

`AmbientOcclusionCustomEffect` は `[CustomEffect(2)]` の 2 入力エフェクトです。入力 0 に元映像、入力 1 に位相パスの出力を受け取ります。各プロパティは代入時にシェーダーが前提とする範囲へ制限します。

| プロパティ | 型 | 範囲 |
|---|---|---|
| `Strength` | `float` | 0〜1 |
| `Radius` | `float` | 1〜256 |
| `HeightGain` | `float` | 0〜8 |
| `Directions` | `float` | 2〜16 |
| `Samples` | `float` | 1〜12 |
| `ShadowR` / `ShadowG` / `ShadowB` | `float` | 0〜1 |
| `Suppression` | `float` | 0〜1 |

`ConstantBuffer` のレイアウトは以下のとおりです。末尾に 3 つの詰め物を置き、合計 48 バイトを 16 バイトの倍数に揃えます。

| フィールド | 型 | 説明 |
|---|---|---|
| `Strength` | `float` | 強度 |
| `Radius` | `float` | 半径 |
| `HeightGain` | `float` | 高さ |
| `Directions` | `float` | 方向数 |
| `Samples` | `float` | サンプル数 |
| `ShadowR` / `ShadowG` / `ShadowB` | `float` | 陰の色 R/G/B |
| `Suppression` | `float` | 模様抑制 |
| `Pad0`〜`Pad2` | `float` | 詰め物 |

`MapInputRectsToOutputRect` は入力 0 の矩形をそのまま出力矩形とします。`MapOutputRectToInputRects` は入力 0・1 の矩形を半径分だけ拡張します。拡張量は `ceil(clamp(radius, 1, 256)) + 2` です。

シェーダーリソース: `pack://application:,,,/AmbientOcclusion;component/Shaders/AmbientOcclusion.cso` と `pack://application:,,,/AmbientOcclusion;component/Shaders/AmbientOcclusionPhase.cso`（いずれも ps_5_0、`ShaderResourceUri.Get` が生成）

---

### 3. エフェクト定義

`AmbientOcclusionEffect` は YMM4 の映像エフェクトとして宣言されます。

`[VideoEffect]` 属性は以下のパラメーターで宣言されます。

- 表示名：`Texts.AmbientOcclusionEffectName`（ローカライズキー、日本語では「環境遮蔽」）
- カテゴリー：`VideoEffectCategories.Filtering`
- 検索タグ：`TagAmbientOcclusion`・`TagShading`・`TagDepth`
- `IsAviUtlSupported = false` により AviUtl 向け EXO 出力は非対応
- `ResourceType = typeof(Texts)` でローカライズリソースを指定

`Label` プロパティは `Texts.AmbientOcclusionEffectName` を返します。

公開プロパティは以下のとおりです。

| プロパティ | 型 | デフォルト | 内部範囲 | アニメーション |
|---|---|---|---|---|
| `Strength` | `Animation` | 50 | 0〜100 | あり |
| `Radius` | `Animation` | 24 | 1〜256 | あり |
| `Height` | `Animation` | 50 | 0〜400 | あり |
| `TextureSuppression` | `Animation` | 50 | 0〜100 | あり |
| `ShadowColor` | `Color` | `#FF1A1420` | — | なし |
| `Directions` | `Animation` | 8 | 2〜16 | あり |
| `Samples` | `Animation` | 6 | 1〜12 | あり |

`GetAnimatables` は `Strength`・`Radius`・`Height`・`TextureSuppression`・`Directions`・`Samples` を返します。

`CreateExoVideoFilters` は空のシーケンスを返します（EXO 非対応）。`CreateVideoEffect` は映像処理用のインスタンスを生成します。

---

### 4. フレームごとの更新

`AmbientOcclusionEffectProcessor` は位相パスと本体パスを接続します。`CreateEffect` で両エフェクトを生成し、位相パスの出力を本体パスの入力 1 へ、元映像を位相パスの入力 0 と本体パスの入力 0 へ接続します。位相パスの `Sensitivity` は既定の 1 のままです。

各フレームで YMM4 の `EffectDescription` からフレーム位置、アイテム長、FPS を取得し、アニメーション値を評価します。前フレームと値が異なる項目だけを本体パスへ転送します。

| パラメータ | 変換 |
|---|---|
| `Strength` | `value / 100` に陰の色の不透明度を掛ける |
| `Radius` | px のまま |
| `Height` | `value / 100` を `HeightGain` へ |
| `TextureSuppression` | `value / 100` を `Suppression` へ |
| `Directions` | 四捨五入して整数へ |
| `Samples` | 四捨五入して整数へ |
| `ShadowColor` | `R/G/B` を 0〜1 の float へ変換 |

入力は各エフェクトへ `SetInput` で接続し、エフェクトチェーンのクリア時はすべての入力を `null` に戻します。

---

### 5. ローカライズ

`Texts` クラスは `[AutoGenLocalizer]` 属性を持つ `partial` クラスとして宣言されます。
`YukkuriMovieMaker.Generator` のソースジェネレーターが `Texts.csv` を処理し、各ロケールのリソースファイルを自動生成します。

対応リソース：日本語（`ja-jp`）・英語（`en-us`）・中国語簡体字（`zh-cn`）・中国語繁体字（`zh-tw`）・韓国語（`ko-kr`）・スペイン語（`es-es`）・アラビア語（`ar-sa`）・インドネシア語（`id-id`）

ローカライズキーの一覧は以下のとおりです。

| キー | ja-jp |
|---|---|
| `AmbientOcclusionEffectName` | 環境遮蔽 |
| `TagAmbientOcclusion` | 環境遮蔽 |
| `TagShading` | 陰影 |
| `TagDepth` | 立体感 |
| `AmbientOcclusionStrength` | 強度 |
| `AmbientOcclusionRadius` | 半径 |
| `AmbientOcclusionHeight` | 高さ |
| `AmbientOcclusionTextureSuppression` | 模様抑制 |
| `AmbientOcclusionShadowColor` | 陰の色 |
| `AmbientOcclusionDirections` | 方向数 |
| `AmbientOcclusionSamples` | サンプル数 |
