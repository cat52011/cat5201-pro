using System.Runtime.CompilerServices;

// 版面決策（DeckLayout / ChooseLayout / FindKeyFigure…）是內部細節，不對外公開 API，
// 但要能被測試釘住——只開放給測試組件。
[assembly: InternalsVisibleTo("cat5201.Tests")]
