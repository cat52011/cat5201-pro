# -*- coding: utf-8 -*-
"""產生 PowerPoint 版簡報，內容與 deck.html 相同。
用法（在專案根目錄）：python website/tools/build_pptx.py
需要：python-pptx、ffmpeg（用來把插畫邊緣淡出成透明）。
"""
import os, subprocess, tempfile
from pptx import Presentation
from pptx.util import Emu, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.enum.dml import MSO_LINE
from pptx.oxml.ns import qn

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.dirname(HERE)
IMG = os.path.join(SITE, 'assets', 'img')
APP = os.path.join(SITE, 'assets', 'app')
OUT = os.path.join(SITE, 'cat5201-pro-簡報.pptx')
TMP = tempfile.mkdtemp(prefix='cat5201_pptx_')

FONT = 'Noto Serif TC'
MONO = 'Courier New'
PAPER, LIGHT, INK, SOFT = 'F3E1C4', 'FBF3E2', '2A2019', '5A4533'
CAMEL, DEEP, TEAL = '9C7A54', '6B5238', '2F5E5C'
NIGHT, NTEXT, NSOFT, RULE, GOLD = '19110B', 'F3E1C4', 'C9B595', 'D8C29C', 'D9B37F'

def px(v): return Emu(int(round(v * 7620)))     # 舞台 1600×900 px → 13.333×7.5 吋
def pt(v): return Pt(round(v * 0.6, 1))          # 網頁 px 字級 → 簡報 pt
def rgb(h): return RGBColor.from_string(h)

def faded(name, alpha, width=1280):
    """把插畫邊緣淡出成透明，存成 PNG。"""
    out = os.path.join(TMP, name + '.png')
    vf = "scale=%d:-2,format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='%s'" % (width, alpha)
    subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-i', os.path.join(IMG, name + '.jpg'), '-vf', vf, out], check=True)
    return out

EDGE = "255*clip(min(X,W-X)/(0.11*W),0,1)*clip(min(Y,H-Y)/(0.10*H),0,1)"

prs = Presentation()
prs.slide_width, prs.slide_height = px(1600), px(900)
BLANK = prs.slide_layouts[6]

def new(bg=PAPER):
    s = prs.slides.add_slide(BLANK)
    f = s.background.fill; f.solid(); f.fore_color.rgb = rgb(bg)
    return s

def setfont(run, face, size, color, bold=False):
    run.font.size = pt(size); run.font.bold = bold; run.font.color.rgb = rgb(color); run.font.name = face
    rPr = run._r.get_or_add_rPr()
    for tag in ('a:ea', 'a:cs'):
        el = rPr.find(qn(tag))
        if el is None:
            el = rPr.makeelement(qn(tag), {}); rPr.append(el)
        el.set('typeface', face)

def text(s, x, y, w, h, paras, size=28, color=INK, bold=False, face=FONT, align=PP_ALIGN.LEFT, lh=1.5, anchor=MSO_ANCHOR.TOP, after=0):
    """paras：字串或 list。每個元素是一段；元素可以是 [(文字, {覆寫})...] 的 run 清單。"""
    tb = s.shapes.add_textbox(px(x), px(y), px(w), px(h))
    tf = tb.text_frame; tf.word_wrap = True; tf.vertical_anchor = anchor
    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
    if isinstance(paras, str): paras = [paras]
    for i, para in enumerate(paras):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.alignment = align; p.line_spacing = lh; p.space_after = pt(after)
        runs = para if isinstance(para, list) else [(para, {})]
        for t, o in runs:
            r = p.add_run(); r.text = t
            setfont(r, o.get('face', face), o.get('size', size), o.get('color', color), o.get('bold', bold))
    return tb

def line(s, x1, y1, x2, y2, color=RULE, weight=1, dash=False):
    c = s.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, px(x1), px(y1), px(x2), px(y2))
    c.line.color.rgb = rgb(color); c.line.width = Pt(weight)
    if dash: c.line.dash_style = MSO_LINE.DASH
    return c

def box(s, x, y, w, h, fill=LIGHT, border=RULE):
    b = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, px(x), px(y), px(w), px(h))
    b.fill.solid(); b.fill.fore_color.rgb = rgb(fill)
    b.line.color.rgb = rgb(border); b.line.width = Pt(1.25); b.shadow.inherit = False
    return b

def pic(s, path, x, y, w=None, h=None, crop_v=0.0):
    p = s.shapes.add_picture(path, px(x), px(y), px(w) if w else None, px(h) if h else None)
    if crop_v:
        p.crop_top = p.crop_bottom = crop_v
        p.height = px(900); p.top = 0
    return p

def h2(s, x, y, w, t, size=84, color=INK):
    lines = t.split('\n')
    return text(s, x, y, w, 110 * len(lines), lines, size=size, color=color, bold=True, lh=1.12)

def numbered(s, x, y, w, items, size=30, gap=74, two_line=False):
    line(s, x, y, x + w, y, INK, 1.5)
    for i, it in enumerate(items):
        yy = y + i * gap
        text(s, x, yy + 14, 60, gap, str(i + 1), size=40, color=CAMEL, lh=1.0)
        if two_line:
            text(s, x + 62, yy + 12, w - 62, gap, [[(it[0], {'bold': True, 'size': 34})], [(it[1], {'size': 25, 'color': SOFT})]], lh=1.35)
        else:
            text(s, x + 62, yy + 18, w - 62, gap, it, size=size, lh=1.2)
        line(s, x, yy + gap, x + w, yy + gap)

def notes(s, t): s.notes_slide.notes_text_frame.text = t

def pageno(s, n, dark=False):
    text(s, 1420, 858, 150, 24, '%02d / 17' % n, size=18, color=NSOFT if dark else DEEP, face=MONO, align=PP_ALIGN.RIGHT, lh=1.0)

# 1 封面
s = new()
pic(s, os.path.join(IMG, 'hero.jpg'), 0, 0, w=1600, crop_v=(1600 / 1.5 - 900) / 2 / (1600 / 1.5))
text(s, 110, 196, 620, 330, ['看得見', '的 AI'], size=150, bold=True, lh=1.02)
text(s, 110, 528, 620, 70, '每一個答案，都有來歷。', size=44, color=DEEP)
text(s, 110, 640, 620, 90, ['cat5201-pro　畢業專題報告', '系所・姓名・日期（請改這一行）'], size=22, color=DEEP, lh=1.7)
notes(s, '各位老師好。開始之前想先問一個問題：你上一次問 AI，知道回答你的是哪一個模型嗎？知道它查了哪些資料嗎？知道那一次花了多少錢嗎？我原本也不知道。這就是我做這個專題的原因。')

# 2 三個問題
s = new(NIGHT)
pic(s, faded('night', "255*clip(X/(0.36*W),0,1)"), 250, 0, h=900)
text(s, 110, 196, 900, 330, ['這個答案，是誰寫的？', '它查過什麼？', '花了多少？'], size=80, color=NTEXT, bold=True, lh=1.3)
text(s, 110, 560, 800, 130, ['你每天都在問 AI 問題。', '但這三件事，它從來沒有告訴過你。'], size=34, color=NSOFT, lh=1.6)
pageno(s, 2, True)

# 3 問題
s = new()
h2(s, 110, 290, 640, '蓋子關著，\n你只拿得到\n最後那張紙條')
x, y, w = 840, 190, 650
line(s, x, y, x + w, y, INK, 1.5)
for i, (b, d) in enumerate([('它會答錯', '而你不知道這一題是哪個模型答的。'), ('它會編來源', '而你看不到它實際查了什麼。'), ('帳單月底才出現', '而你不知道剛才那一次花了多少。')]):
    yy = y + i * 170
    text(s, x, yy + 26, w, 130, [[(b, {'bold': True, 'size': 40})], [(d, {'size': 28, 'color': SOFT})]], lh=1.45)
    line(s, x, yy + 170, x + w, yy + 170)
pageno(s, 3)

# 4 主張
s = new(NIGHT)
text(s, 110, 270, 1380, 240, [[('AI 給你答案。', {})], [('cat5201-pro 連帳一起給你。', {'color': GOLD})]], size=88, color=NTEXT, bold=True, lh=1.24)
text(s, 110, 540, 1300, 70, '大家都在比誰的 AI 比較聰明。這個專題不比聰明，比的是交代。', size=34, color=NSOFT)
pageno(s, 4, True)

# 5 一張畫布
s = new()
h2(s, 110, 250, 460, '一張畫布')
text(s, 110, 380, 440, 170, '每個節點是一次 AI 互動。你在上面提問，它把過程留在上面。', size=30, lh=1.65)
text(s, 110, 580, 460, 130, ['Windows 桌面程式　WPF　.NET 10', 'OpenAI　Anthropic', 'Google Gemini　Perplexity'], size=21, color=DEEP, face=MONO, lh=1.8)
pic(s, os.path.join(APP, 'canvas.png'), 614, 190, w=986)
text(s, 614, 722, 400, 30, '程式實際畫面', size=19, color=DEEP)
pageno(s, 5)

# 6 決策窗
s = new()
pic(s, os.path.join(APP, 'decision.png'), 110, 92, h=716)
h2(s, 630, 96, 860, '一個答案\n的來歷')
numbered(s, 630, 340, 860, ['這是什麼任務', '選了哪個模型，為什麼', '查過哪些來源', '用了多少 token、花了多少錢', '每一步收到的輸入'], gap=66)
text(s, 630, 706, 860, 70, '你不必相信它。你可以檢查它。', size=40, color=TEAL)
pageno(s, 6)

# 7 串聯
s = new()
h2(s, 110, 90, 1200, '把提問連起來')
text(s, 110, 214, 900, 60, '上一步做完，下一步自己接著跑。', size=34)
for i, (t, tag) in enumerate([('查資料', ''), ('找出矛盾', '{{input}}'), ('寫成報告', '{{input}}')]):
    bx = 110 + i * 545
    box(s, bx, 340, 290, 130)
    text(s, bx + 34, 362, 230, 50, t, size=34, bold=True, lh=1.2)
    if tag: text(s, bx + 34, 418, 230, 34, tag, size=22, color=DEEP, face=MONO, lh=1.2)
    if i < 2: line(s, bx + 290, 405, bx + 545, 405, CAMEL, 2.25, dash=True)
line(s, 110, 560, 1490, 560, INK, 1.5)
for i, (b, d) in enumerate([('拉一條線', '從節點角落的小圓點拉出去，就多一張下游的卡。'), ('駝色虛線', '在線上按右鍵切換。虛線代表上游完成後自動執行下游。'), ('{{input}}', '執行時換成上游的回答。多個上游會等全部完成才開始。')]):
    text(s, 110 + i * 478, 594, 420, 220, [[(b, {'bold': True, 'size': 31, 'color': INK})], [(d, {})]], size=25, color=SOFT, lh=1.6)
pageno(s, 7)

# 8 協作
s = new()
pic(s, faded('duo', EDGE), 820, 90, h=720)
h2(s, 110, 150, 700, '讓模型\n互相檢查')
line(s, 110, 400, 740, 400, INK, 1.5)
for i, t in enumerate(['各自的回答', '交叉檢視', '共識與分歧']):
    yy = 400 + i * 84
    text(s, 110, yy + 14, 60, 70, str(i + 1), size=44, color=CAMEL, lh=1.0)
    text(s, 180, yy + 18, 560, 60, t, size=36, bold=True, lh=1.2)
    line(s, 110, yy + 84, 740, yy + 84)
text(s, 110, 684, 640, 120, ['勾選 2 到 4 個模型。它們哪裡不一致，分開列給你看。', '呼叫次數是模型數的兩倍，兩個模型約 NT$3～5。'], size=24, color=SOFT, lh=1.7)
pageno(s, 8)

# 9 成品
s = new()
pic(s, faded('press', EDGE, 1024), -10, 36, h=828)
h2(s, 800, 110, 690, '交得出東西')
x, y, w = 800, 250, 690
line(s, x, y, x + w, y, INK, 1.5)
for i, (a, b) in enumerate([('PowerPoint', '簡報'), ('Word', '書面報告'), ('Excel', '表格'), ('PDF', '文件'), ('Code', '程式碼'), ('Image・Video', '圖片與影片')]):
    yy = y + i * 72
    text(s, x, yy + 12, 400, 56, a, size=36, lh=1.2)
    text(s, x + 390, yy + 20, 300, 50, b, size=26, color=SOFT, align=PP_ALIGN.RIGHT, lh=1.2)
    line(s, x, yy + 72, x + w, yy + 72)
text(s, 800, 712, 690, 60, '哪一頁不滿意，只改那一頁：「第 3 頁改成比較表」。', size=24, color=SOFT)
pageno(s, 9)

# 10 分析與審查
s = new()
pic(s, faded('check', EDGE, 1024), 802, 36, h=828)
h2(s, 110, 96, 700, '數字來自\n實際執行')
yy = 330
for b, d in [('沙箱資料分析', '附上 CSV 或 Excel，它真的執行程式：讀完整資料、分析、繪圖，另附可重跑的 .py。'),
             ('獨立審查', '寫完後另開一次獨立呼叫審查，Auto 模式由不同廠商的模型負責。對照數字與表格，另存一份審查清單。只列問題，不改報告。'),
             ('照實說', '審查沒回應或只抽查了一部分，清單會寫明，不會顯示成「沒有問題」。')]:
    text(s, 110, yy, 650, 160, [[(b, {'bold': True, 'size': 31, 'color': INK})], [(d, {})]], size=25, color=SOFT, lh=1.6)
    yy += 164
pageno(s, 10)

# 11 帳
s = new()
pic(s, faded('ledger', "255*clip((0.96*W-X)/(0.30*W),0,1)*clip(min(Y,H-Y)/(0.12*H),0,1)"), -230, 63, h=774)
h2(s, 800, 110, 690, '帳')
text(s, 800, 250, 690, 30, '實測約略花費', size=20, color=DEEP, bold=True)
x, y, w = 800, 292, 690
line(s, x, y, x + w, y, INK, 1.5)
for i, (a, b) in enumerate([('一般問答', 'NT$1 以下'), ('協作模式，兩個模型', 'NT$3–5'), ('3～5 頁簡報，快速版', '約 NT$2'), ('5 頁簡報，最佳品質加配圖', 'NT$40–50'), ('附資料檔的分析報告', 'NT$20–25'), ('報告完成後的獨立審查', 'NT$1–12')]):
    yy = y + i * 74
    text(s, x, yy + 20, 470, 50, a, size=28, lh=1.2)
    text(s, x + 440, yy + 20, 250, 50, b, size=27, bold=True, face=MONO, align=PP_ALIGN.RIGHT, lh=1.2)
    line(s, x, yy + 74, x + w, yy + 74, CAMEL, 1, dash=(i < 5))
pageno(s, 11)

# 12 三道關
s = new()
h2(s, 110, 290, 440, '成本不會\n失控的\n三道關')
numbered(s, 570, 216, 520, [('每日上限', '預設 NT$100，會超過時先停下來問。'), ('執行前的確認框', '列出要產生的檔案與約略費用。'), ('每筆即時入帳', '填好儲值餘額後，每筆花費當下扣除並顯示。')], gap=156, two_line=True)
pic(s, os.path.join(APP, 'confirm.png'), 1150, 330, w=360)
text(s, 1150, 574, 360, 30, '程式實際畫面', size=19, color=DEEP)
pageno(s, 12)

# 13 架構
s = new()
h2(s, 110, 90, 900, '怎麼做的')
for i, (a, b, c, core) in enumerate([('WPF 桌面前端', 'net10.0-windows', '畫布、節點、決策窗、手機鏡像伺服器', False),
                                     ('cat5201.Core', 'net10.0，不引用任何 WPF 型別', '模型註冊與路由、成本估算、任務編排、決策紀錄、輸出判斷、獨立審查', True),
                                     ('xUnit 測試', '1,239 項', '路由、成本、審查、重試、執行安全', False)]):
    yy = 236 + i * 144
    box(s, 110, yy, 1180, 128, fill=INK if core else LIGHT, border=INK if core else RULE)
    text(s, 144, yy + 36, 330, 60, a, size=34, bold=True, color=LIGHT if core else INK, lh=1.2)
    text(s, 470, yy + 22, 800, 96, [[(b, {'face': MONO, 'size': 21, 'color': GOLD if core else DEEP})], [(c, {})]], size=23, color=NSOFT if core else SOFT, lh=1.5)
text(s, 110, 690, 1180, 120, '核心與介面分開，所以移植到 macOS、Linux 或網頁版，主要是重寫介面層。金鑰以 Windows DPAPI 加密存在本機，不會上傳。', size=24, color=SOFT, lh=1.7)
pageno(s, 13)

# 14 故事
s = new(NIGHT)
text(s, 110, 330, 780, 240, ['看不見的東西，', '沒有辦法改進。'], size=88, color=NTEXT, bold=True, lh=1.24)
x, y, w = 900, 258, 590
line(s, x, y, x + w, y, CAMEL)
for i, (a, b) in enumerate([('988', '筆呼叫紀錄，一筆一筆記下來'), ('US$9.32', '紀錄裡發現的單次文件最高花費'), ('US$1.03', '回頭調整後，11 月的平均')]):
    yy = y + i * 128
    text(s, x, yy + 30, 290, 80, a, size=60, color=GOLD, lh=1.0)
    text(s, x + 300, yy + 34, 290, 80, b, size=24, color=NSOFT, lh=1.5)
    line(s, x, yy + 128, x + w, yy + 128, CAMEL)
notes(s, '為了知道錢花去哪裡，我把每一次呼叫都記了下來。也是靠這些紀錄，我才發現有一次產一份文件花了九美元，回頭把它壓到一美元出頭。這件事讓我確定：看不見的東西，沒有辦法改進。（數字發表前請用當時的資料再核對一次。）')
pageno(s, 14, True)

# 15 限制
s = new()
h2(s, 110, 390, 640, '還沒做好的')
x, y, w = 840, 180, 650
line(s, x, y, x + w, y, INK, 1.5)
yy = y
for t, hgt in [('只支援 Windows。', 92), ('「最佳品質」的簡報與報告約需 5 分鐘。', 92), ('Gemini 無法即時搜尋，要由其他模型代查。', 92), ('手機鏡像走區網內未加密的 HTTP。', 92), ('數字單位檢查只比對聊天中的回答，不檢查產出的檔案。', 140)]:
    text(s, x, yy + 24, w, hgt, t, size=31, lh=1.5)
    yy += hgt
    line(s, x, yy, x + w, yy)
pageno(s, 15)

# 16 示範
s = new()
h2(s, 110, 390, 640, '現場示範')
numbered(s, 840, 168, 650, ['問一個需要查資料的問題', '打開決策窗，看它為什麼這樣答', '拉出下游節點，讓結果往下做', '讓兩個模型互相檢查', '請它交一份簡報，看確認框上的費用', '這一趟總共花了多少'], size=31, gap=94)
pageno(s, 16)

# 17 結尾
s = new()
p = pic(s, faded('close', "255*clip((0.62*W-X)/(0.20*W),0,1)", 1600), 0, 0, w=1600, crop_v=(1600 / 1.5 - 900) / 2 / (1600 / 1.5))
text(s, 900, 250, 620, 300, ['AI 會越來越能幹。', '所以更要看得見，', '它在做什麼。'], size=64, bold=True, lh=1.3)
text(s, 900, 590, 620, 90, ['cat5201-pro　看得見的 AI', '謝謝各位。'], size=22, color=DEEP, lh=1.8)
notes(s, 'AI 會越來越能幹，所以更要看得見它在做什麼。謝謝各位。')

prs.save(OUT)
print('saved', OUT, len(prs.slides.__iter__.__self__._sldIdLst), 'slides')
