- 131080 / type=26 = set “platform performance mode” (you’ll see inputs like 255,48,1,0 or 255,4,1,0).
  - 131080 / type=39 = SetMaxFan() toggle (input should be a single byte like 0/1; it’s commonly invoked while syncing/applying modes).
  - 131080 / type=34 = set per-mode GPU/power behavior payloads (the 1,1,1,87 pattern you saw matches this one).
  - 131080 / type=45 (out=128) and 131080 / type=46 (in=128) look like “read/write a 128-byte settings blob” that happens around the
    background’s restore/apply-settings flow (your log shows type=46 right after Entry::RestoreStorageSettings).
  - 131080 / type=35 shows up as 0,0,0,0 a lot; likely a companion/handshake/reset-type call in that same settings-sync sequence (not one
    you’ve mapped yet)