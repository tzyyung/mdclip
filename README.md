# MdViewer

Windows 上的 Markdown 閱讀器：雙擊 `.md` 就打開，左邊檔案樹、右邊大綱，彩色排版，
複製一個路徑它就自動跳出那份文件。

![icon](MdViewer/app.ico)

## 安裝

需要 64 位元 Windows 10 / 11。

1. 編譯（見下一節），或直接用已經編好的 `app\MdViewer.exe`。
2. 執行一次 `.\install.ps1`：之後雙擊 `.md`、`.markdown` 都會用 MdViewer 打開，開始功能表也會有捷徑。
   不需要系統管理員權限，只影響目前的使用者。
   - 如果 Windows 還是用別的程式開，表示之前選過「一律使用這個應用程式」：
     在 `.md` 上按右鍵 → 開啟檔案 → 選擇其他應用程式 → MdViewer → 一律使用。
   - 移除：`.\install.ps1 -Uninstall`

`app\MdViewer.exe` 需要 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。
要拿到沒有裝 .NET 的電腦上用，請編譯成自帶執行環境的版本（`dist\MdViewer.exe`，約 50 MB）。
兩者都需要 WebView2 Runtime（Windows 10 / 11 通常已內建）。

## 編譯

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
.\publish.ps1                 # -> app\MdViewer.exe
.\publish.ps1 -SelfContained  # -> dist\MdViewer.exe，換電腦用
```

MdViewer 正在執行時，`publish.ps1` 會先請它正常結束（`MdViewer.exe --exit`），編譯完再開回來。
不要用工作管理員強制結束：系統匣會留下沒有程式的圖示（滑鼠移過去才會消失）。

## 功能

在 MdViewer 裡按 `?` 可以看到完整的快捷鍵與功能表。

**閱讀**
- 標題、粗體、表格、引用、清單都有顏色層次；12 種主題（GitHub、Dracula、Nord、Solarized……），`Ctrl+D` 亮暗切換。
- 程式碼上色（C#、Python、PowerShell、JSON、SQL 等）、Mermaid 流程圖、KaTeX 數學公式（`$...$`、`$$...$$`）。
- 檔案開頭的 YAML frontmatter 顯示成可收合的「文件資訊」。
- 點圖片放大檢視（滾輪縮放、拖曳移動）；圖片檔本身也能直接打開。
- 程式碼右上角可以複製；標題左邊的 🔗 會複製「檔案路徑#標題」，貼回來會直接跳到那一節。
- 文字大小 `Ctrl+=` / `Ctrl+-` / `Ctrl+0`，或 `Ctrl+滾輪`。
- 匯出 PDF（更多選單），保留程式碼顏色和流程圖。

**找檔案、找內容**
- `Ctrl+P` 快速開啟：模糊比對檔名、直接貼上路徑，或用 `#` 跳到本篇的標題。
- `Ctrl+Shift+F` 搜尋資料夾裡所有文件的內文，點結果會跳到那一處。
- `Ctrl+F` 在本篇中尋找。
- `Ctrl+H` 閱讀紀錄：最近 30 個檔案，跨資料夾，打開會回到上次讀到的位置。
- 把檔案或資料夾拖到視窗上就打開。

**分頁與版面**
- `Ctrl+T` 新分頁、`Ctrl+W` 關閉、`Ctrl+Tab` 切換；`Ctrl+點` 或滑鼠中鍵在新分頁開啟。每個資料夾會記住自己的分頁。
- `Alt+←` / `Alt+→`（或滑鼠側鍵）上一頁 / 下一頁，每個分頁各自記錄。
- `Ctrl+B` 檔案側欄、`Ctrl+J` 大綱、`F11` 專注閱讀；拖曳邊線調整寬度，雙擊恢復。

**剪貼簿與背景執行**
- 複製的文字或檔案裡有 `.md` 或圖片的路徑時自動打開：絕對路徑、相對路徑、只有檔名都可以
  （只有檔名時會到開過的資料夾裡找）。視窗會跳到最前面，但不會搶走你正在打字的焦點。
  可在「更多」選單或系統匣改成「只提示」或「不理會」。
- 按 ✕ 會縮到右下角系統匣，繼續看剪貼簿；要結束請在系統匣圖示按右鍵 →「結束」。
- 系統匣選單：最近讀過的檔案、剪貼簿模式、開機時在背景啟動。

**安全**：文件裡的 `<script>`、`onerror=` 之類的事件和 `javascript:` 連結一律不執行。

## 設定與記錄檔

都在 `%LOCALAPPDATA%\MdViewer\`：

| 檔案 | 內容 |
|---|---|
| `theme.txt` | 主題、剪貼簿模式、上次的資料夾、記住的資料夾、閱讀紀錄 |
| `error.log` | 錯誤記錄；超過 1 MB 會換成 `error.old.log` 重新開始 |
| `WebView2\` | 內嵌瀏覽器的資料 |

版面（側欄寬度、文字大小、分頁）存在內嵌瀏覽器裡，跟著資料夾記住。

## 測試

```powershell
.\tools\smoke-test.ps1
```

先跑單元測試（`MdViewer.Tests`，約 60 個「複製這段文字應該打開哪個檔」的例子：絕對 / 相對路徑、只有檔名、
含空白的路徑、黏在中文句子裡的路徑、`#標題`），再用 `tools\fixtures\` 的測試文件把主要功能跑一遍，
最後檢查系統匣：✕ 會縮到系統匣、複製路徑會叫回視窗、`--exit` 能結束。需要 Node.js 22 以上。
只想跑單元測試（幾秒鐘，不會動到正在執行的 MdViewer）：`dotnet test MdViewer.Tests`。
執行前會先關掉 MdViewer，結束後開回原本那份文件；剪貼簿會先備份、測完還原，測試檔不會留在閱讀紀錄裡。

## 程式結構

| 路徑 | 內容 |
|---|---|
| `MdViewer/Program.cs` | 進入點、只允許一個程式（第二次開啟會把路徑交給已開的視窗）、`--tray` / `--exit` |
| `MdViewer/MainForm.cs` | 視窗、系統匣、剪貼簿監控、Markdown 轉 HTML（Markdig）、提供網頁內容給 WebView2 |
| `MdViewer/PathFinder.cs` | 從複製或貼上的文字找出要打開的檔案（剪貼簿、快速開啟共用） |
| `MdViewer/Themes.cs` | 12 種主題的顏色、設定檔的讀寫 |
| `MdViewer/web/` | 介面本體（HTML / CSS / JS），以及打包進 exe 的 highlight.js、Mermaid、KaTeX |
| `tools/make-icon.ps1` | 產生 `app.ico` |
| `MdViewer.Tests/` | `PathFinder` 的單元測試 |
| `tools/smoke-test.ps1`、`tools/smoke.mjs` | 自動化測試（含單元測試） |
