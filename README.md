# ⛂ Checkers Chinook & KingsRow REST Web API (.NET 8)

High-performance REST Web API service for 8x8 English Draughts (American Checkers) built on **.NET 8**, integrating the native 64-bit **KingsRow / Chinook** engine, endgame tablebases (2–6 pieces), and opening books.

The project is fully engineered to satisfy all technical specification requirements and is ready for production hosting on **Windows Server** under **IIS**.

---

## 📋 Table of Contents
- [Architecture](#-architecture)
- [API Endpoints](#-api-endpoints)
- [Checkers Rules & PDN Notation](#-checkers-rules--pdn-notation)
- [Difficulty Levels & Limits](#-difficulty-levels--limits)
- [Quick Start & Build](#-quick-start--build)
- [Automated Acceptance Tests](#-automated-acceptance-tests)
- [Interactive Web Test Bench](#-interactive-web-test-bench)
- [Production Deployment on IIS](#-production-deployment-on-iis)
- [Configuration (appsettings.json)](#-configuration-appsettingsjson)

---

## 🏗 Architecture

```
CheckersApp/
├── Checkers.Api/             # Core ASP.NET Core 8 Web API service
│   ├── Controllers/         # MoveController (/v1/move/suggest, /v1/move/validate, /healthz)
│   ├── Services/            # CheckersGameRules, EnginePool, EngineAdapter, PdnService, LruCache
│   ├── Models/              # Request & Response DTO contracts
│   └── wwwroot/             # Interactive web test bench (index.html)
├── ChinookWorker/            # Console worker process (IPC via Stdin/Stdout)
│   ├── CheckerBoard.cs      # P/Invoke bindings for Kingsrow64.dll (CheckerBoard API)
│   ├── PdnHelper.cs         # Square 1–32 <-> 8x8 board coordinate mapper
│   └── Program.cs           # Command protocol (POSITION / SEARCH) and metrics parser
├── Engines/                  # Native 64-bit libraries, opening books, and tablebases
│   ├── Kingsrow64.dll       # Native checkers engine by Ed Gilbert
│   ├── egdb64.dll           # Endgame database driver
│   ├── db/                  # Endgame tablebase files (db2..db6 for 2–6 pieces)
│   └── *.bin, *.odb         # Neural net weights and opening books
└── CheckersApp.slnx          # Solution file
```

### Key Engineering Decisions:
1. **Native C++ Code Isolation**: P/Invoke calls are isolated inside dedicated worker processes (`chinook.exe`). If an unhandled native memory exception (such as `AccessViolationException`) occurs, it never crashes the ASP.NET Core web server.
2. **Persistent Warm Worker Pool**: On application startup (`IHostedService`), 2 long-lived Chinook worker processes are initialized, pre-warmed, and kept running. Workers are never spawned per request.
3. **Round-Robin with Async Concurrency Control**: Requests are scheduled across workers via Round-Robin with non-blocking concurrency synchronization using `SemaphoreSlim(1,1)` per worker.
4. **Self-Healing Safeguards**: If a client cancels a request or a search exceeds `hardTimeMs`, the corresponding worker process is immediately recycled and replaced with a fresh worker, preventing stdin/stdout stream desynchronization.
5. **Two-Tier Checkers Rule Validation**: Both engine suggestions and user moves are validated against official checkers rules via the built-in `CheckersGameRules` engine.
6. **Canonical LRU Caching**: PDN positions are normalized and piece numbers are sorted canonically, ensuring high cache hit ratios (TTL 15 minutes, capacity 20,000 positions).

---

## 🔌 API Endpoints

### 1. `POST /v1/move/suggest`
Calculates the best move for a given position in PDN format.

**Request:**
```json
{
  "gameId": "checkers-8x8",
  "state": {
    "notation": "PDN",
    "position": "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"
  },
  "level": "strong",
  "limits": {
    "maxDepth": 16,
    "softTimeMs": 550,
    "hardTimeMs": 1200
  }
}
```

**Response (HTTP 200 OK):**
```json
{
  "engine": "chinook",
  "bestMove": "14x23",
  "pv": ["14x23", "27x18", "16x23", "25-21", "12-16", "28-24"],
  "scoreOrWDL": 132,
  "depth": 18,
  "nodes": 746548,
  "positionKey": "pdn:B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16",
  "info": {
    "tablebaseHit": false,
    "timeMs": 188
  }
}
```

*Status Codes:*
- `200 OK`: Successful move calculation or LRU cache hit.
- `422 Unprocessable Entity`: Invalid PDN notation (out-of-range squares, empty board, >12 pieces per side).
- `504 Gateway Timeout`: Search exceeded `hardTimeMs`.
- `500 Internal Server Error`: Engine failed to compute a legal move.

---

### 2. `POST /v1/move/validate`
Validates a move against official checkers rules.

**Request:**
```json
{
  "position": "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16",
  "move": "14x23"
}
```

**Response (HTTP 200 OK):**
```json
{
  "legal": true
}
```

> **Note:** If a capture (jump) is available on the board, quiet moves (e.g., `10-15`) will return `legal: false` because **jumping is mandatory in checkers**.

---

### 3. `GET /healthz`
Checks service health and active worker count.

**Response (HTTP 200 OK):**
```json
{
  "ok": true,
  "workers": 2
}
```

---

## 🏁 Checkers Rules & PDN Notation

1. **Who Moves First:**
   - In English Draughts / American Checkers (the ruleset used by Chinook and KingsRow), **BLACK moves first**!
   - In PDN notation, turn is indicated by the first prefix:
     - `B:` — Black to move (e.g., initial position `B:W21..32:B1..12`).
     - `W:` — White to move.
2. **Mandatory Capture (Jumping):**
   - If any player piece can make a jump over an opponent's piece, jumping is strictly mandatory. Quiet moves are illegal in that position.
3. **Endgame Tablebases ($\le 8$ pieces):**
   - When 8 or fewer pieces remain on the board, KingsRow probes the endgame database (`tablebaseHit: true`), returning in **less than 50 ms** (typically 1–10 ms).

---

## ⚡ Difficulty Levels & Limits

| Level | Search Depth | Soft Time (ms) | Hard Timeout (ms) | Characteristics |
| :--- | :--- | :--- | :--- | :--- |
| **`weak`** | 8 | 100 ms | 1200 ms | Fast move selection without randomness |
| **`medium`** | 12 | 250 ms | 1200 ms | Balanced depth and responsiveness |
| **`strong`** | 16 | 550 ms | 1200 ms | Deep tactical search + Tablebase priority |
| **$\le 8$ pieces** | 6 | 20–40 ms | 1200 ms | Immediate Tablebase Probe (< 50 ms) |

---

## 🚀 Quick Start & Build

### Prerequisites:
- Windows 10 / 11 or Windows Server 2019 / 2022 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. One-Click Launcher:
In the project root, run:
- **`Start_Checkers_App.bat`**

*(If the standalone executable is not built yet, the batch file will automatically run `build_all.bat` first, start the API server, warm up 2 Chinook workers, and launch your browser at `http://127.0.0.1:5114`.)*

### 2. Build & Publish from Source:
Run the build script:
```cmd
CheckersApp\build_all.bat
```
or via the command line:
```cmd
dotnet build CheckersApp/CheckersApp.slnx -c Release
dotnet publish CheckersApp/Checkers.Api/Checkers.Api.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
*(MSBuild automatically builds `chinook.exe`, packages `Checkers.Api.exe`, and deploys the native DLLs/databases to `publish/Workers`).*

### 3. Run via .NET CLI:
```cmd
dotnet run --project CheckersApp/Checkers.Api/Checkers.Api.csproj -c Release
```
Once started, the API and Web UI are available at: `http://127.0.0.1:5114`

---

## 🧪 Automated Acceptance Tests

To verify all Acceptance Criteria automatically, execute the PowerShell test script:

```powershell
powershell -ExecutionPolicy Bypass -File CheckersApp/test_api.ps1
```

**The suite validates:**
1. `GET /healthz` $\rightarrow$ OK, 2 active workers.
2. Tablebase ($\le 8$ pieces) $\rightarrow$ Elapsed time < 50 ms, `tablebaseHit: true`.
3. Midgame (level `strong`) $\rightarrow$ Elapsed time < 600 ms, legal best move `14x23`, full PV line.
4. Move validation for legal jump `14x23` $\rightarrow$ `legal: true`.
5. Move validation for quiet move `10-15` when a jump exists $\rightarrow$ `legal: false`.
6. Invalid PDN $\rightarrow$ HTTP `422 Unprocessable Entity`.

---

## 🖥 Interactive Web Test Bench

The service embeds an interactive graphical test bench.
Open in your browser: **`http://127.0.0.1:5114/`**

**Features:**
- Interactive $8 \times 8$ draughts board with checkers and kings.
- Quick position presets:
  - Initial setup
  - Midgame (from specification)
  - Endgame $\le 8$ pieces (Tablebase test)
  - Invalid PDN (HTTP 422 test)
- Interactive turn toggle (`⚫ Black (B:)` / `⚪ White (W:)`).
- Point-and-click move testing for real-time validation.
- Live engine metrics display (elapsed ms, Tablebase hit status, nodes, depth, score, PV line).

---

## 🌐 Production Deployment on IIS

1. **Prerequisites on Windows Server:**
   - Install the **ASP.NET Core 8.0 Hosting Bundle**.
2. **Publish the Project:**
   ```cmd
   dotnet publish CheckersApp/Checkers.Api/Checkers.Api.csproj -c Release -o C:\inetpub\checkers-api
   ```
3. **Configure IIS:**
   - Create a new Site or Application in IIS Manager pointing to `C:\inetpub\checkers-api`.
   - Set the Application Pool properties:
     - **.NET CLR Version**: `No Managed Code`
     - **Enable 32-Bit Applications**: `False` (Kingsrow64 requires 64-bit mode).
4. **Permissions:**
   - Grant the Application Pool identity (`IIS AppPool\<Pool_Name>`) **Read & Execute** permissions on `C:\inetpub\checkers-api` (specifically the `Workers` subfolder).

---

## ⚙ Configuration (appsettings.json)

Located at `CheckersApp/Checkers.Api/appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Engine": {
    "Type": "chinook",
    "Path": "Workers\\chinook.exe",
    "Workers": 2,
    "Databases": "Engines\\db"
  },
  "Cache": {
    "Capacity": 20000,
    "TtlMinutes": 15
  },
  "Limits": {
    "DefaultSoftTimeMs": 300,
    "DefaultHardTimeMs": 1200
  }
}
```
