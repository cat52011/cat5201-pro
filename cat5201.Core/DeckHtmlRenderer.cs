using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Cat5201;

/// <summary>Native preview from the same geometry as PPTX/PDF. Editing controls stay outside the slide.</summary>
public static class DeckHtmlRenderer
{
    public static string Build(PresentationOutlinePayload outline, IReadOnlyList<byte[]?>? slideImages = null, bool allowEdit = false)
    {
        var pages = DeckScene.Build(outline, slideImages?.FirstOrDefault());
        var html = new StringBuilder("<!doctype html><html lang='zh-TW'><meta charset='utf-8'><style>body{margin:0;padding:24px;background:#e9e9e7;font-family:Microsoft JhengHei}svg{display:block;width:100%;max-width:1100px;margin:0 auto 16px;box-shadow:0 3px 12px #0002}details{max-width:1068px;margin:0 auto 24px;padding:16px;background:white}textarea{box-sizing:border-box;width:100%;font:inherit;margin:8px 0}button{margin-right:12px;padding:8px 16px}label{display:block;margin-top:12px}</style><body>");
        string Esc(string s) => WebUtility.HtmlEncode(s);
        var shownEditors = new HashSet<int>();
        int pageNumber = 0;
        foreach (var page in pages)
        {
            pageNumber++;
            html.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 540'>");
            foreach (var e in page.Elements)
            {
                string coordinates = FormattableString.Invariant($"x='{e.X}' y='{e.Y}' width='{e.W}' height='{e.H}'");
                if (e.Kind == "rect") html.Append($"<rect {coordinates} fill='#{e.Color}'/>");
                else if (e.Kind == "image") html.Append($"<image {coordinates} href='data:image/png;base64,{Convert.ToBase64String(e.Image!)}'/>");
                else html.Append(FormattableString.Invariant($"<text x='{e.X}' y='{e.Y + e.Size}' font-family='{DeckScene.Font}' font-size='{e.Size}' font-weight='{(e.Bold ? 700 : 400)}' fill='#{e.Color}'>{Esc(e.Text)}</text>"));
            }
            html.Append("</svg>");
            if (allowEdit && shownEditors.Add(page.SourceOrder))
            {
                var source = outline.Slides.First(s => s.Order == page.SourceOrder);
                html.Append($"<details data-order='{source.Order}' data-page='{pageNumber}'><summary>編輯文字</summary><label>標題<textarea class='heading' rows='2'>{Esc(source.Heading)}</textarea></label><label>重點（每行一項）<textarea class='bullets' rows='5'>{Esc(string.Join("\n", source.Bullets))}</textarea></label><button type='button' data-action='edit'>儲存文字</button><button type='button' data-action='regen'>重新生成這頁</button></details>");
            }
        }
        if (allowEdit) html.Append("<script>document.addEventListener('click',e=>{const b=e.target.closest('button[data-action]');if(!b||!window.chrome?.webview)return;const d=b.closest('details'),action=b.dataset.action;window.chrome.webview.postMessage(JSON.stringify({action,order:Number(action==='regen'?d.dataset.page:d.dataset.order),heading:d.querySelector('.heading').value,bullets:d.querySelector('.bullets').value.split('\\n').filter(x=>x.trim())}));});</script>");
        return html.Append("</body></html>").ToString();
    }
}
