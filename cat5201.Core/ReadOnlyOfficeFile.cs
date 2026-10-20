using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;

namespace Cat5201
{
    /// <summary>
    /// 以「別人也開著也沒關係」的方式開啟 Office 檔案。
    ///
    /// <c>PresentationDocument.Open(path, false)</c> 這種吃路徑的多載，內部是
    /// <c>File.Open(..., FileShare.Read)</c>——只要 PowerPoint 或 LibreOffice 正開著同一個檔，
    /// 我們這邊就會收到 IOException: being used by another process。
    /// 2026-10-15 實測：使用者按了「用系統程式開啟」看簡報，回來再按預覽就變成
    /// 「這份簡報讀不開 / 檔案可能損壞」——檔案好得很，只是被 LibreOffice 開著。
    ///
    /// 讀取不該有這種限制：自己開 FileStream 並讓出 ReadWrite 共用權，
    /// 別的程式在編輯也照樣讀得到（讀到的是當下的內容）。
    /// 寫入就不行了，那本來就該要求對方先關檔。
    /// </summary>
    public sealed class ReadOnlyOfficeFile<TDocument> : IDisposable
        where TDocument : OpenXmlPackage
    {
        private readonly FileStream _stream;

        public TDocument Document { get; }

        private ReadOnlyOfficeFile(FileStream stream, TDocument document)
        {
            _stream = stream;
            Document = document;
        }

        internal static ReadOnlyOfficeFile<TDocument> Create(string path, Func<Stream, TDocument> open)
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            try
            {
                return new ReadOnlyOfficeFile<TDocument>(stream, open(stream));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Document.Dispose();
            _stream.Dispose();
        }
    }

    public static class ReadOnlyOfficeFile
    {
        public static ReadOnlyOfficeFile<PresentationDocument> Pptx(string path)
            => ReadOnlyOfficeFile<PresentationDocument>.Create(path, s => PresentationDocument.Open(s, false));

        public static ReadOnlyOfficeFile<WordprocessingDocument> Docx(string path)
            => ReadOnlyOfficeFile<WordprocessingDocument>.Create(path, s => WordprocessingDocument.Open(s, false));

        public static ReadOnlyOfficeFile<SpreadsheetDocument> Xlsx(string path)
            => ReadOnlyOfficeFile<SpreadsheetDocument>.Create(path, s => SpreadsheetDocument.Open(s, false));
    }
}
