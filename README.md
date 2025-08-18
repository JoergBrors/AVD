# AVD – Azure Virtual Desktop Tools & Scripts

Dieses Repository enthält **Skripte und Hilfstools** für die Bereitstellung, Pflege und Automatisierung einer Azure Virtual Desktop (AVD) Umgebung.

## 📂 Repository-Struktur

```
AVD/
│── VM/
│   ├── Deploy-W11-FromSIG.ps1        # Erstellt eine VM aus der Shared Image Gallery
│   ├── Publish-GalleryVersionFromGolden.ps1 # Publiziert Golden Images in die Shared Image Gallery
│   └── readme.md                     # Detaillierte Infos zu den VM-Skripten
│
├── LICENSE                           # Lizenzinformationen
└── README.md                         # Diese Datei (Allgemeine Repo-Übersicht)
```

## 🚀 Quickstart

1. Repository klonen:
   ```bash
   git clone https://github.com/<dein-org>/AVD.git
   cd AVD/VM
   ```

2. Vorbereitungen:
   - Installiere die **Azure PowerShell Module** (`Az.Accounts`, `Az.Compute`, `Az.Network`, `Az.Resources`)
   - Stelle sicher, dass du im richtigen Azure Subscription Context bist:
     ```powershell
     Connect-AzAccount
     Select-AzSubscription -SubscriptionId <SUB-ID>
     ```

3. Beispiel: VM aus SIG deployen
   ```powershell
   .\Deploy-W11-FromSIG.ps1 -ResourceGroupName "RG-AVD" -Location "westeurope" -ImageDefinition "Win11-AVD" -ImageVersion "latest"
   ```

## 📖 Dokumentation

- [VM/readme.md](./VM/readme.md) – Detaillierte Infos zu den PowerShell-Skripten
- [Projektdefinition (Markdown)](./readme.md) – Ausführliche AVD Projektbeschreibung inkl. Architekturdiagrammen

## 🤝 Contribution

- Pull Requests willkommen
- Nutze Issues für Bug Reports oder Feature Requests
- Bitte halte dich an den vorhandenen Code- und Dokumentationsstil

## 📜 Lizenz

Dieses Projekt steht unter der **MIT License** – siehe [LICENSE](./LICENSE).
