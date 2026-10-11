# Chat IA dans Revit / In-Revit AI chat

> Ajout de ce fork (ris-ros95-cpu/rvt-mcp). Base : [bimwright/rvt-mcp](https://github.com/bimwright/rvt-mcp), Apache-2.0.

## FR

Ruban **Add-Ins → RvtMcp → Chat IA** ouvre un panneau ancrable (dockable) à droite.

- **Fournisseur** : *Claude* (clé Anthropic) ou *OpenAI / Codex* (clé OpenAI, modèles `gpt-5`, `gpt-5-codex`, … ; champ modèle éditable).
- **Clés API** : bouton « Clé API… ». Chiffrées avec **Windows DPAPI** (utilisateur courant) dans `%LOCALAPPDATA%\Bimwright\rvt-mcp\chat\*.key`, jamais en clair. Repli : variables `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`.
- **Outils** : le panneau lance le même `rvt-mcp.exe` que Claude/Codex (MCP stdio, épinglé sur ce processus Revit avec `--target pid:<pid>`). Mêmes outils, mêmes garde-fous (mode lecture seule, `send_code` désactivé par défaut, `batch_execute` annulable en un Ctrl+Z).
- **Niveaux de risque** (annotations MCP) : *lecture* → exécuté directement ; *écriture* → fenêtre de confirmation (option « toujours autoriser pendant la session ») ; *destructif* et *code C#* → confirmation à chaque fois, avec les arguments affichés.
- OpenAI limite une requête à 128 outils : les outils de lecture sont envoyés en priorité. Activez moins de toolsets si besoin.

**Pourquoi des clés API et pas la session Codex CLI / Claude Code ?** Ces CLI n'exposent pas d'API locale stable pour piloter une conversation depuis une autre application, et leurs jetons de connexion ne doivent pas être réutilisés par un tiers. Pour utiliser votre abonnement Codex / Claude Code, lancez-les dans un terminal : ils sont déjà connectés à Revit par `install.ps1`.

## EN

**Add-Ins → RvtMcp → Chat IA** toggles a dockable pane. Pick Claude (Anthropic key) or OpenAI/Codex models (OpenAI key). Keys are DPAPI-encrypted per Windows user. The pane is an MCP client of the same `rvt-mcp.exe`, so it gets the exact same tools and safety gates; read-only calls run directly, write calls ask for approval, destructive/code calls always ask. CLI sessions (Codex CLI, Claude Code) are not driven from the pane because they offer no stable local API and their login tokens must not be reused; use them from a terminal, where the installer already wired them.

## Test status

Compiled on Linux against Revit 2026 / 2025 API reference packages. Not yet exercised inside a running Revit — see the PR checklist.
