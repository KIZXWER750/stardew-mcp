# Local Stardew Valley Wiki knowledge cache

Run `python tools/wiki_sync.py` from the repository root to generate structured JSON files in the `en` directory.

Generated JSON is intentionally excluded from Git. It contains normalized facts and provenance rather than complete article text. Wiki-derived output is licensed under CC BY-NC-SA 3.0 and must retain its source URL, page revision, attribution, and license metadata.

Live game state and installed game/mod assets take precedence over this cache.
