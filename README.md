# 🎬 VPlayer — WPF Video Player (.NET 8 / LibVLCSharp)

Ứng dụng phát video đa định dạng cho Windows 10/11, xây dựng bằng WPF + .NET 8 + LibVLCSharp.

---

## ✨ Tính năng

| **Tính năng** | Chi tiết |
|-----------|----------|
| **Codec** | MP4, MKV, AVI, MOV, WMV, FLV, WebM, M4V, TS, VOB, OGV, 3GP, MPG, RMVB, DIVX, HEVC/H.265, H.264, ASF, F4V, MXF và nhiều hơn nữa |
| **Playlist** | Thêm nhiều file/thư mục, xóa từng item, xóa tất cả |
| **Điều khiển** | Play/Pause, Stop, Previous, Next, Seek (←/→), Volume 0–200% |
| **Tốc độ phát** | 0.25x, 0.5x, 0.75x, 1x, 1.25x, 1.5x, 2x, 2.5x, 3x, 4x (Menu + phím tắt [ / ]) |
| **Chế độ** | Lặp lại (Repeat), Ngẫu nhiên (Shuffle) |
| **UI** | Dark theme, custom title bar, fullscreen, kéo thả file |
| **Phím tắt** | Space, F, M, Ctrl+O, ←/→, ↑/↓, [ / ], Escape |

---

## 🔧 Yêu cầu

- **Windows 10/11** (x64)
- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
- **NuGet packages** (tự động tải khi build):
  - `LibVLCSharp.WPF` (3.9.0) — wrapper .NET cho libVLC
  - `VideoLAN.LibVLC.Windows` (3.0.21) — libvlc native DLLs (bao gồm đầy đủ codec)
  - `CommunityToolkit.Mvvm` (8.3.2)
  - `Microsoft.Xaml.Behaviors.Wpf` (1.1.135)

---

## 🚀 Build & Chạy

```bash
# Clone / giải nén project
cd WpfVideoPlayer

# Restore packages
dotnet restore

# Chạy thẳng (debug)
dotnet run

# Build release
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

Sau khi publish, chạy `WpfVideoPlayer.exe` trong thư mục `./publish`.

---

## ⌨️ Phím tắt

| Phím | Chức năng |
|------|-----------|
| `Space` | Play / Pause |
| `F` | Toàn màn hình |
| `Escape` | Thoát toàn màn hình |
| `M` | Tắt/bật âm |
| `Ctrl+O` | Mở file |
| `←` / `→` | Tua lùi / tiến 5 giây |
| `↑` / `↓` | Tăng / giảm âm lượng |
| `[` / `]` | Giảm / tăng tốc độ phát |

---

## 📁 Cấu trúc project

```
WpfVideoPlayer/
├── App.xaml / App.xaml.cs
├── Models/
│   └── PlaylistItem.cs
├── ViewModels/
│   └── MainViewModel.cs       ← toàn bộ logic
├── Views/
│   ├── MainWindow.xaml        ← UI layout
│   └── MainWindow.xaml.cs     ← code-behind (drag&drop, fullscreen...)
├── Converters/
│   └── Converters.cs          ← IValueConverter helpers
├── Themes/
│   └── DarkTheme.xaml         ← style, màu sắc, control templates
└── WpfVideoPlayer.csproj
```

---

## 🎨 Về LibVLCSharp

LibVLCSharp bọc **libvlc** — engine phía sau VLC Media Player — cung cấp:
- Hỗ trợ hơn **100 định dạng** container và codec
- Hardware decoding (DXVA2, D3D11) trên Windows
- Phát stream: HTTP, RTSP, HLS, YouTube (với plugin)

Package `VideoLAN.LibVLC.Windows` chứa các native DLL của libvlc, không cần cài VLC riêng.

---

## 🤖 CI/CD — GitHub Actions

### Cách release phiên bản mới

```bash
git add .
git commit -m "feat: your changes"
git tag v1.0.0
git push origin main --tags
```

→ GitHub Actions tự động: **build → package → tạo installer → đính kèm vào Release**

### Cách hoạt động

```
push tag v1.2.3
    │
    ▼
windows-latest runner
    ├── dotnet restore          (cache NuGet)
    ├── dotnet publish -r win-x64
    ├── Install Inno Setup 6
    ├── ISCC.exe installer/setup.iss
    │       └── VPlayer-Setup-1.2.3.exe
    └── Create GitHub Release
            └── attach installer EXE
```

### Manual build (không tạo Release)

Actions tab → **Build & Release** → **Run workflow** → nhập version tùy chọn.
Installer được lưu 30 ngày dưới dạng workflow artifact.

### Yêu cầu repo

- Repo phải **public** (hoặc GitHub Pro/Team cho private)  
- Không cần thêm secret — dùng `GITHUB_TOKEN` tự động
- File `.github/workflows/build-release.yml` phải ở nhánh default
