using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Cat5201;

/// <summary>
/// 預覽：跟 pptx / PDF 吃同一份幾何，所以看到的就是成品。
/// 編輯介面掛在投影片外面（不會混進版面），並且跟著這份簡報的強調色，看起來像同一件作品的一部分。
/// </summary>
public static class DeckHtmlRenderer
{
    public static string Build(PresentationOutlinePayload outline, IReadOnlyList<byte[]?>? slideImages = null, bool allowEdit = false)
    {
        var cover = slideImages?.FirstOrDefault();
        var pages = DeckScene.Build(outline, cover);
        var art = DeckArtDirection.ForTopic(outline.Title + " " + outline.Topic);
        string imageCost = ModelCostEstimator.ImageUnitCostText(AgentRuntime.DeckImageSize, "high");

        var html = new StringBuilder("<!doctype html><html lang='zh-TW'><meta charset='utf-8'><style>")
            .Append(Css(art))
            .Append("</style><body>");

        string Esc(string s) => WebUtility.HtmlEncode(s);
        var shownEditors = new HashSet<int>();
        int pageNumber = 0;

        foreach (var page in pages)
        {
            pageNumber++;
            html.Append("<section class='slide'>");
            html.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 540'>");
            foreach (var e in page.Elements)
            {
                string coordinates = FormattableString.Invariant($"x='{e.X}' y='{e.Y}' width='{e.W}' height='{e.H}'");
                if (e.Kind == "rect")
                    html.Append(FormattableString.Invariant($"<rect {coordinates} fill='#{e.Color}' fill-opacity='{e.Alpha / 100f}'/>"));
                else if (e.Kind == "image")
                    html.Append($"<image {coordinates} preserveAspectRatio='xMidYMid slice' href='data:image/png;base64,{Convert.ToBase64String(e.Image!)}'/>");
                else
                {
                    // 字體／對齊／字距都跟 pptx 用同一組值，預覽才會等於成品。
                    string fonts = $"{(string.IsNullOrEmpty(e.Font) ? DeckScene.Font : e.Font)}, {(string.IsNullOrEmpty(e.EaFont) ? DeckScene.Font : e.EaFont)}, {DeckScene.Font}";
                    float x = e.Align == "r" ? e.X + e.W : e.Align == "c" ? e.X + e.W / 2 : e.X;
                    string anchor = e.Align == "r" ? "end" : e.Align == "c" ? "middle" : "start";
                    html.Append(FormattableString.Invariant(
                        $"<text x='{x}' y='{e.Y + e.Size}' text-anchor='{anchor}' font-family='{fonts}' font-size='{e.Size}' font-weight='{(e.Bold ? 700 : 400)}' letter-spacing='{e.Track}' fill='#{e.Color}'>{Esc(e.Text)}</text>"));
                }
            }
            html.Append("</svg>");

            if (allowEdit)
            {
                var source = outline.Slides.First(s => s.Order == page.SourceOrder);

                // 內容放不下時會拆成續頁。續頁一樣要能編輯（它就是同一張投影片的後半段），
                // 之前只有第一頁有編輯區，續頁等於被鎖住。
                bool continuation = !shownEditors.Add(page.SourceOrder);

                html.Append($"<details data-order='{source.Order}' data-page='{pageNumber}'>")
                    .Append($"<summary><span class='pg'>{pageNumber:00}</span><span class='ttl'>{Esc(Shorten(source.Heading, 28))}{(continuation ? "（接續）" : "")}</span><span class='act'>編輯這一頁</span></summary>")
                    .Append("<div class='panel'>")
                    .Append(continuation
                        ? "<p class='note'>這一頁是上一頁的接續，屬於同一張投影片；在這裡編輯或重新生成，會一起更新兩頁。</p>"
                        : "")
                    .Append("<div class='col'>")
                    .Append($"<label>標題<textarea class='heading' rows='2' spellcheck='false'>{Esc(source.Heading)}</textarea></label>")
                    .Append($"<label>重點（一行一項，可直接增刪字）<textarea class='bullets' rows='6' spellcheck='false'>{Esc(string.Join("\n", source.Bullets))}</textarea></label>")
                    .Append("<div class='row'><button type='button' class='primary' data-action='edit'>儲存文字</button>")
                    .Append("<span class='hint'>只改文字，版面與其他頁不動（可用「回到上一版」還原）</span></div>")
                    .Append("</div>")
                    .Append("<div class='col'>")
                    .Append("<label>重新生成這一頁<textarea class='instruction' rows='3' spellcheck='false' placeholder='想怎麼改？例如：改寫得更精簡、多強調成本效益、換一個切入角度（留空＝照原需求重寫）'></textarea></label>")
                    .Append("<div class='row'><button type='button' data-action='regen'>只重新生成這一頁</button>")
                    .Append("<span class='hint'>會呼叫模型改寫這一頁的文字，其他頁不受影響；要求裡提到「圖」時會一併生成配圖</span></div>")
                    .Append("</div>");

                // 配圖：換一張、加一張、或拿掉。文字與圖分開重生，不必為了換圖把整頁重寫。
                bool isCoverSlide = string.Equals(source.Kind, "cover", StringComparison.OrdinalIgnoreCase);
                bool hasImage = isCoverSlide ? cover is { Length: > 0 } : source.ImageBytes is { Length: > 0 };

                html.Append("<div class='imgrow'>")
                    .Append($"<label>這一頁的配圖{(hasImage ? "（目前有一張）" : "（目前沒有圖）")}")
                    .Append("<textarea class='image-brief' rows='2' spellcheck='false' placeholder='想要什麼畫面？例如：清晨霧氣中的湖面與岩石、紙與金屬的質感靜物（留空＝依這一頁的內容自動決定）'></textarea></label>")
                    .Append($"<div class='row'><button type='button' data-action='image'>{(hasImage ? "換一張配圖" : "生成配圖")}</button>")
                    .Append(hasImage ? "<button type='button' data-action='imageRemove'>移除配圖</button>" : "")
                    .Append($"<span class='hint'>生成一張圖會實際呼叫圖片模型（{imageCost}）；地圖、國旗、商標這類查得到正確答案的東西不會生成</span></div>")
                    .Append("</div>")
                    .Append("<p class='status' hidden></p></div></details>");
            }
            html.Append("</section>");
        }

        if (allowEdit)
            html.Append("<script>")
                .Append("document.addEventListener('click',e=>{")
                .Append("const b=e.target.closest('button[data-action]');if(!b||!window.chrome?.webview)return;")
                .Append("const d=b.closest('details'),action=b.dataset.action;")
                .Append("const s=d.querySelector('.status');")
                .Append("d.querySelectorAll('button').forEach(x=>x.disabled=true);")
                .Append("s.hidden=false;s.textContent={regen:'正在重新生成這一頁…完成後預覽會自動更新。',")
                .Append("image:'正在生成這一頁的配圖…圖片模型通常要一兩分鐘。',")
                .Append("imageRemove:'正在移除配圖並重建檔案…'}[action]||'正在存回並重建 PDF…';")
                .Append("window.chrome.webview.postMessage(JSON.stringify({action,")
                .Append("order:Number(action==='edit'?d.dataset.order:d.dataset.page),")
                .Append("heading:d.querySelector('.heading').value,")
                .Append("instruction:action==='image'?d.querySelector('.image-brief').value:d.querySelector('.instruction').value,")
                .Append("bullets:d.querySelector('.bullets').value.split('\\n').filter(x=>x.trim())}));});")
                .Append("document.addEventListener('keydown',e=>{if((e.ctrlKey||e.metaKey)&&e.key==='Enter'){")
                .Append("const d=e.target.closest('details');if(d)d.querySelector('button[data-action=\"edit\"]').click();}});")
                .Append("</script>");

        return html.Append("</body></html>").ToString();
    }

    private static string Shorten(string? text, int max)
    {
        string t = (text ?? "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    private static string Css(DeckArtDirection art) =>
        "*{box-sizing:border-box}" +
        "body{margin:0;padding:28px 24px 64px;background:#ECECE9;font-family:'Microsoft JhengHei',system-ui,sans-serif;color:#1B1B19;-webkit-font-smoothing:antialiased}" +
        ".slide{max-width:1100px;margin:0 auto 28px}" +
        "svg{display:block;width:100%;background:#fff;box-shadow:0 2px 6px #0000001a,0 12px 28px #00000014}" +
        "details{margin-top:10px;background:#fff;border:1px solid #DFDFDA;border-radius:6px;overflow:hidden}" +
        "details[open]{border-color:#" + art.Accent + "55}" +
        "summary{display:flex;align-items:center;gap:12px;padding:11px 16px;cursor:pointer;list-style:none;font-size:13px;user-select:none}" +
        "summary::-webkit-details-marker{display:none}" +
        "summary:hover{background:#F6F6F3}" +
        ".pg{font:600 12px/1 'Segoe UI';letter-spacing:1.5px;color:#" + art.Accent + "}" +
        ".ttl{flex:1;color:#44443F;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
        ".act{color:#77776F;font-size:12px}" +
        "details[open] .act::after{content:'（收合）'}" +
        ".panel{display:grid;grid-template-columns:1fr 1fr;gap:22px;padding:4px 16px 18px;border-top:1px solid #EDEDE8}" +
        ".col{display:flex;flex-direction:column;min-width:0}" +
        "label{display:block;margin-top:14px;font-size:12px;color:#6E6E66;letter-spacing:.3px}" +
        "textarea{display:block;width:100%;margin-top:6px;padding:10px 12px;font:14px/1.7 'Microsoft JhengHei';color:#1B1B19;" +
        "background:#FBFBF9;border:1px solid #DDDDD7;border-radius:4px;resize:vertical}" +
        "textarea:focus{outline:none;border-color:#" + art.Accent + ";background:#fff;box-shadow:0 0 0 3px #" + art.Accent + "22}" +
        ".row{display:flex;align-items:center;gap:12px;margin-top:12px;flex-wrap:wrap}" +
        "button{padding:8px 18px;font:13px 'Microsoft JhengHei';color:#2A2A26;background:#fff;" +
        "border:1px solid #CFCFC8;border-radius:4px;cursor:pointer}" +
        "button:hover:not(:disabled){background:#F4F4F0;border-color:#B6B6AE}" +
        "button.primary{color:#fff;background:#" + art.Accent + ";border-color:#" + art.Accent + "}" +
        "button.primary:hover:not(:disabled){filter:brightness(1.08)}" +
        "button:disabled{opacity:.45;cursor:default}" +
        ".imgrow{grid-column:1/-1;padding-top:6px;border-top:1px solid #EFEFEA}" +
        ".note{grid-column:1/-1;margin:10px 0 0;padding:8px 11px;font-size:11.5px;line-height:1.6;" +
        "color:#6E6E66;background:#F6F6F3;border-radius:5px}" +
        ".hint{font-size:11.5px;color:#88887F;line-height:1.5}" +
        ".status{grid-column:1/-1;margin:2px 0 0;padding:9px 12px;font-size:12.5px;color:#" + art.Accent + ";" +
        "background:#" + art.Accent + "10;border-radius:4px}" +
        "@media(max-width:820px){.panel{grid-template-columns:1fr}}";
}
