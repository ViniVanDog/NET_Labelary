# NET Labelary
### ✨ Features
- Drag & Drop `.zip` files → automatic extraction and processing
- Drag & Drop `.txt` files → direct processing with no extraction required
- Proper Labelary API free-tier compliance:
  - Automatic batching of ZPL payloads
  - Rate-limiting between requests
  - Safe handling of large inputs

Only works on Windows, .NET Framework 4.8

---

### ⚠ Known Issue

- Some label providers generate ZPL that embeds **large base64 image blocks instead of vector ZPL commands**, Labelary will hit you back with **ERROR: Total size of all embedded fonts and images exceeds the maximum allowed (2 MB)** after uploading large amount of this type of label.
