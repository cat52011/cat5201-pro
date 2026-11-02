# _example_smoke：計分器的煙霧測試資料（**不是實驗結果**）

這個資料夾裡的 outputs/*.txt 全部是手寫的假產出，metrics.csv 的數字全部是 0。
存在的唯一目的是證明計分器會對不同品質的輸入給出不同的分數——
包含「五點只寫三點」會扣涵蓋率、「踩到捏造」會直接不及格。

真實的 run 請另開 `docs/eval/runs/<日期>/`，不要寫進這裡。

重跑：

```
dotnet run --project tools/EvalScorer -- score --run docs/eval/runs/_example_smoke
```
