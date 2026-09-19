#!/usr/bin/env python3
"""Build a local, attributed Stardew Valley Wiki fact cache.

The cache contains normalized facts and provenance, never full article text.
Wiki-derived output is CC BY-NC-SA 3.0; see the generated manifest and LICENSE.txt.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import time
import urllib.parse
import urllib.request
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

API = "https://stardewvalleywiki.com/mediawiki/api.php"
PAGE_BASE = "https://stardewvalleywiki.com/"
IMPORTER_VERSION = "1.0.0"
LICENSE = "CC BY-NC-SA 3.0"
USER_AGENT = "StardewMCP-WikiSync/1.0 (local structured knowledge cache)"

VILLAGERS = [
    "Abigail", "Alex", "Caroline", "Clint", "Demetrius", "Dwarf", "Elliott", "Emily",
    "Evelyn", "George", "Gus", "Haley", "Harvey", "Jas", "Jodi", "Kent", "Krobus",
    "Leah", "Leo", "Lewis", "Linus", "Marnie", "Maru", "Pam", "Penny", "Pierre",
    "Robin", "Sam", "Sandy", "Sebastian", "Shane", "Vincent", "Willy", "Wizard",
]
SHOPS = [
    "Pierre's General Store", "JojaMart", "Carpenter's Shop", "Fish Shop", "Blacksmith",
    "Marnie's Ranch", "The Stardrop Saloon", "Oasis", "Traveling Cart", "Adventurer's Guild",
    "Island Trader", "Desert Trader", "Bookseller",
]
CRAFTABLES = [
    "Chest", "Furnace", "Scarecrow", "Sprinkler", "Quality Sprinkler", "Iridium Sprinkler",
    "Preserves Jar", "Keg", "Mayonnaise Machine", "Cheese Press", "Bee House", "Seed Maker",
    "Oil Maker", "Loom", "Tapper", "Heavy Tapper", "Lightning Rod", "Crystalarium",
    "Recycling Machine", "Worm Bin", "Slime Egg-Press", "Charcoal Kiln",
]


class WikiClient:
    def __init__(self, delay: float = 0.15):
        self.delay = delay

    def request(self, params: dict) -> dict:
        params = {**params, "format": "json", "formatversion": "2"}
        url = API + "?" + urllib.parse.urlencode(params)
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
        last_error = None
        for attempt in range(3):
            try:
                with urllib.request.urlopen(request, timeout=45) as response:
                    data = json.load(response)
                time.sleep(self.delay)
                return data
            except Exception as exc:  # network failures are retried, then reported
                last_error = exc
                time.sleep(1 + attempt * 2)
        raise RuntimeError(f"Wiki request failed: {last_error}")

    def category_pages(self, root: str, max_depth: int = 3) -> set[str]:
        queue = deque([(root, 0)])
        seen, pages = set(), set()
        while queue:
            category, depth = queue.popleft()
            if category in seen or depth > max_depth:
                continue
            seen.add(category)
            continuation = None
            while True:
                params = {"action": "query", "list": "categorymembers", "cmtitle": "Category:" + category, "cmlimit": "500"}
                if continuation:
                    params["cmcontinue"] = continuation
                data = self.request(params)
                for member in data.get("query", {}).get("categorymembers", []):
                    if member.get("ns") == 0:
                        pages.add(member["title"])
                    elif member.get("ns") == 14:
                        queue.append((member["title"].removeprefix("Category:"), depth + 1))
                continuation = data.get("continue", {}).get("cmcontinue")
                if not continuation:
                    break
        return pages

    def pages(self, titles: list[str]) -> list[dict]:
        result = []
        for start in range(0, len(titles), 20):
            data = self.request({
                "action": "query", "prop": "revisions", "rvprop": "ids|timestamp|content", "rvslots": "main",
                "redirects": "1", "titles": "|".join(titles[start:start + 20]),
            })
            for page in data.get("query", {}).get("pages", []):
                revisions = page.get("revisions") or []
                if page.get("missing") or not revisions:
                    continue
                revision = revisions[0]
                result.append({
                    "title": page["title"], "pageId": page.get("pageid"), "revisionId": revision.get("revid"),
                    "revisionTimestamp": revision.get("timestamp", ""),
                    "text": revision.get("slots", {}).get("main", {}).get("content", ""),
                })
        return result

    def page_revisions(self, titles: list[str]) -> list[dict]:
        result = []
        for start in range(0, len(titles), 20):
            data = self.request({
                "action": "query", "prop": "revisions", "rvprop": "ids|timestamp",
                "redirects": "1", "titles": "|".join(titles[start:start + 20]),
            })
            for page in data.get("query", {}).get("pages", []):
                revisions = page.get("revisions") or []
                if page.get("missing") or not revisions:
                    continue
                revision = revisions[0]
                result.append({
                    "title": page["title"], "pageId": page.get("pageid"),
                    "revisionId": revision.get("revid"), "revisionTimestamp": revision.get("timestamp", ""),
                })
        return result


def split_top_level(value: str, delimiter: str = "|") -> list[str]:
    parts, current, curly, square = [], [], 0, 0
    i = 0
    while i < len(value):
        pair = value[i:i + 2]
        if pair == "{{":
            curly += 1
            current.extend(pair)
            i += 2
            continue
        if pair == "}}":
            curly = max(0, curly - 1)
            current.extend(pair)
            i += 2
            continue
        if pair == "[[":
            square += 1
            current.extend(pair)
            i += 2
            continue
        if pair == "]]":
            square = max(0, square - 1)
            current.extend(pair)
            i += 2
            continue
        if value[i] == delimiter and curly == 0 and square == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(value[i])
        i += 1
    parts.append("".join(current))
    return parts


def extract_template(text: str, name_prefix: str) -> tuple[str, dict[str, str]] | None:
    match = re.search(r"\{\{\s*" + re.escape(name_prefix), text, re.IGNORECASE)
    if not match:
        return None
    start, depth, i = match.start(), 0, match.start()
    while i < len(text) - 1:
        pair = text[i:i + 2]
        if pair == "{{":
            depth += 1
            i += 2
            continue
        if pair == "}}":
            depth -= 1
            i += 2
            if depth == 0:
                body = text[start + 2:i - 2]
                pieces = split_top_level(body)
                fields = {}
                for piece in pieces[1:]:
                    if "=" in piece:
                        key, value = piece.split("=", 1)
                        fields[key.strip().lower()] = value.strip()
                return pieces[0].strip(), fields
            continue
        i += 1
    return None


def template_values(value: str, template: str) -> list[str]:
    pattern = re.compile(r"\{\{\s*" + re.escape(template) + r"\s*\|\s*([^|}]+)", re.IGNORECASE)
    return [clean_text(item) for item in pattern.findall(value)]


def named_items(value: str) -> list[dict]:
    """Read {{Name|Item|quantity}} values without retaining surrounding wiki markup."""
    items = []
    for match in re.finditer(r"\{\{\s*Name\s*\|([^{}]+)\}\}", value, re.IGNORECASE):
        pieces = split_top_level(match.group(1))
        name = clean_text(pieces[0]) if pieces else ""
        quantity = number(pieces[1]) if len(pieces) > 1 else None
        if name:
            item = {"name": name}
            if quantity is not None:
                item["quantity"] = quantity
            items.append(item)
    return items


def clean_text(value: str) -> str:
    value = re.sub(r"<!--.*?-->", " ", value, flags=re.DOTALL)
    value = re.sub(r"<ref\b[^>]*>.*?</ref>|<ref\b[^>]*/>", " ", value, flags=re.DOTALL | re.IGNORECASE)
    value = re.sub(r"\[\[(?:File|Image):[^\]]+\]\]", " ", value, flags=re.IGNORECASE)
    value = re.sub(r"\[\[[^\]|]+\|([^\]]+)\]\]", r"\1", value)
    value = re.sub(r"\[\[([^\]]+)\]\]", r"\1", value)
    for template in ("Season", "Name", "NPC", "Weather inline", "Price", "Tprice"):
        value = re.sub(r"\{\{\s*" + re.escape(template) + r"\s*\|\s*([^|}]+)(?:[^}]*)\}\}", r"\1", value, flags=re.IGNORECASE)
    value = re.sub(r"\{\{[^{}]*\}\}", " ", value)
    value = re.sub(r"<[^>]+>", " ", value)
    value = value.replace("'''", "").replace("''", "")
    return re.sub(r"\s+", " ", html.unescape(value)).strip(" •\n\t")


def source(page: dict) -> dict:
    return {
        "type": "stardew_valley_wiki", "pageTitle": page["title"],
        "pageUrl": PAGE_BASE + urllib.parse.quote(page["title"].replace(" ", "_"), safe="()_'"),
        "pageId": page["pageId"], "pageRevision": page["revisionId"],
        "revisionTimestamp": page["revisionTimestamp"], "license": LICENSE,
    }


def number(value: str) -> int | None:
    match = re.search(r"\d[\d,]*", clean_text(value))
    return int(match.group(0).replace(",", "")) if match else None


def seasons(value: str) -> list[str]:
    found = template_values(value, "Season")
    if not found:
        plain = clean_text(value)
        found = [season for season in ("Spring", "Summer", "Fall", "Winter") if re.search(r"\b" + season + r"\b", plain, re.I)]
    return list(dict.fromkeys(found))


def record(page: dict, category: str, facts: dict) -> dict:
    return {
        "subject": page["title"], "subjectId": "", "category": category,
        "facts": facts, "authorityRank": 30, "verificationStatus": "wiki_only",
        "source": source(page),
    }


def parse_crop(page: dict) -> dict | None:
    parsed = extract_template(page["text"], "Infobox")
    if not parsed:
        return None
    name, fields = parsed
    if "fish" in name.lower() or "growth" not in fields or "season" not in fields:
        return None
    raw_season = fields.get("season", "")
    facts = {
        "seasons": seasons(raw_season), "growthDays": number(fields.get("growth", "")),
        "regrowthDays": number(fields.get("regrowth", "")), "seed": clean_text(fields.get("seed", "")),
        "baseSellPrice": number(fields.get("sellprice", "")), "farmingXp": number(fields.get("xp", "")),
    }
    return record(page, "crop", {k: v for k, v in facts.items() if v not in (None, "", [])})


def parse_fish(page: dict) -> dict | None:
    parsed = extract_template(page["text"], "Infobox fish")
    if not parsed:
        return None
    _, fields = parsed
    facts = {
        "locations": [p.strip() for p in re.split(r"[,•]", clean_text(fields.get("location", ""))) if p.strip()],
        "seasons": seasons(fields.get("season", "")), "time": clean_text(fields.get("time", "")),
        "weather": template_values(fields.get("weather", ""), "Weather inline") or [clean_text(fields.get("weather", ""))],
        "difficulty": number(fields.get("difficulty", "")), "behavior": clean_text(fields.get("behavior", "")),
        "size": clean_text(fields.get("size", "")), "baseSellPrice": number(fields.get("price", "")),
    }
    return record(page, "fish", {k: v for k, v in facts.items() if v not in (None, "", [])})


def parse_villager(page: dict) -> dict | None:
    parsed = extract_template(page["text"], "Infobox villager")
    if not parsed:
        return None
    _, fields = parsed
    birthday_raw = fields.get("birthday", "")
    birthday_season = template_values(birthday_raw, "Season")
    birthday_template = re.search(r"\{\{\s*Season\s*\|\s*([^|}]+)\s*\|\s*(\d{1,2})", birthday_raw, re.I)
    birthday_day = int(birthday_template.group(2)) if birthday_template else number(birthday_raw)
    gifts = extract_template(page["text"], "GiftsByItem")
    preferences = {}
    if gifts:
        for key in ("love", "like", "neutral", "dislike", "hate"):
            if key in gifts[1]:
                preferences[key] = [clean_text(p) for p in gifts[1][key].split(",") if clean_text(p)]
    facts = {
        "birthday": {"season": birthday_season[0] if birthday_season else clean_text(birthday_raw), "day": birthday_day},
        "location": clean_text(fields.get("location", "")), "address": clean_text(fields.get("address", "")),
        "marriageCandidate": clean_text(fields.get("marriage", "")).lower() == "yes",
        "favoriteItems": template_values(fields.get("favorites", ""), "Name"), "giftPreferences": preferences,
    }
    return record(page, "villager", {k: v for k, v in facts.items() if v not in (None, "", [], {})})


def first_paragraph(text: str) -> str:
    body = text.lstrip()
    while body.startswith("{{"):
        parsed = extract_template(body, "")
        if not parsed:
            break
        depth, end, i = 0, None, 0
        while i < len(body) - 1:
            pair = body[i:i + 2]
            if pair == "{{":
                depth += 1
                i += 2
                continue
            if pair == "}}":
                depth -= 1
                i += 2
                if depth == 0:
                    end = i
                    break
                continue
            i += 1
        if end is None:
            break
        body = body[end:].lstrip()
    for block in re.split(r"\n\s*\n", body):
        cleaned = clean_text(block)
        if cleaned and not cleaned.startswith(("File:", "__TOC__")):
            return cleaned[:1000]
    return ""


def parse_festival(page: dict) -> dict | None:
    text = page["text"]
    summary = first_paragraph(text)
    if "festival" not in (page["title"] + " " + summary).lower() and page["title"] not in {"Night Market", "SquidFest", "Trout Derby"}:
        return None
    season_match = re.search(r"\[\[(Spring|Summer|Fall|Winter)(?:\|[^]]+)?\]\]", text, re.I)
    day_match = re.search(r"(?:on (?:the )?|takes place on )(\d{1,2})(?:st|nd|rd|th)?[^\n.]{0,80}(?:\[\[(Spring|Summer|Fall|Winter))", text, re.I)
    time_match = re.search(r"between\s+([0-9:]+\s*(?:am|pm))\s+and\s+([0-9:]+\s*(?:am|pm))", clean_text(text[:5000]), re.I)
    facts = {"summary": summary}
    if season_match:
        facts["season"] = season_match.group(1).title()
    if day_match:
        facts["day"] = int(day_match.group(1))
    if time_match:
        facts["entryWindow"] = {"from": time_match.group(1), "to": time_match.group(2)}
    return record(page, "festival", facts)


def parse_shop(page: dict) -> dict:
    text = page["text"]
    sentences = re.split(r"(?<=[.!?])\s+", clean_text(text[:12000]))
    hours = [s for s in sentences if re.search(r"\b(open|hours?)\b", s, re.I) and re.search(r"\d", s)][:5]
    parsed = extract_template(text, "Infobox")
    fields = parsed[1] if parsed else {}
    facts = {
        "summary": first_paragraph(text), "hoursStatements": hours,
        "address": clean_text(fields.get("address", "")), "owner": clean_text(fields.get("owner", "")),
    }
    return record(page, "shop", {k: v for k, v in facts.items() if v not in (None, "", [])})


def parse_craftable(page: dict) -> dict | None:
    parsed = extract_template(page["text"], "Infobox")
    if not parsed:
        return None
    _, fields = parsed
    if "craft" not in clean_text(fields.get("source", "")).lower() and "ingredients" not in fields:
        return None
    ingredients = named_items(fields.get("ingredients", ""))
    facts = {
        "ingredients": ingredients or [clean_text(fields.get("ingredients", ""))],
        "recipeSource": clean_text(fields.get("recipe", "")), "sellPrice": clean_text(fields.get("sellprice", "")),
    }
    return record(page, "crafting", {k: v for k, v in facts.items() if v not in (None, "", [])})


def parse_bundles(page: dict) -> dict:
    names = sorted(set(clean_text(name) for name in re.findall(r"\{\{Bundle\|([^|}]+)", page["text"], re.I)))
    return record(page, "bundle", {"summary": first_paragraph(page["text"]), "referencedBundles": names})


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def read_existing(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if value.get("importerVersion") == IMPORTER_VERSION else {}
    except (OSError, ValueError, AttributeError):
        return {}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path("mod/StardewMCP/data/knowledge/wiki/en"))
    parser.add_argument("--target-game-version", default="current-wiki; validate against installed game data")
    parser.add_argument("--delay", type=float, default=0.15)
    args = parser.parse_args()

    client = WikiClient(args.delay)
    crop_titles = sorted(client.category_pages("Crops"))
    fish_titles = sorted(client.category_pages("Fish"))
    festival_titles = sorted(client.category_pages("Festivals"))
    groups = {
        "crops": (crop_titles, parse_crop), "fish": (fish_titles, parse_fish),
        "villagers": (VILLAGERS, parse_villager), "festivals": (festival_titles, parse_festival),
        "shops": (SHOPS, parse_shop), "crafting": (CRAFTABLES, parse_craftable),
        "bundles": (["Bundles"], parse_bundles),
    }
    retrieved = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    manifest_pages, counts, sync_stats, errors = {}, {}, {}, []
    for category, (titles, parser_fn) in groups.items():
        output_path = args.output / f"{category}.json"
        existing = read_existing(output_path)
        old_pages = existing.get("pages", {})
        old_entries = {item.get("source", {}).get("pageTitle"): item for item in existing.get("entries", [])}
        revisions = client.page_revisions(list(titles))
        page_index = {page["title"]: page for page in revisions}
        unchanged = {title for title, page in page_index.items()
                     if old_pages.get(title, {}).get("revisionId") == page["revisionId"]}
        entries = [entry for title, entry in old_entries.items() if title in unchanged]
        changed = [title for title in page_index if title not in unchanged]
        for page in client.pages(changed):
            try:
                parsed = parser_fn(page)
                if parsed:
                    entries.append(parsed)
            except Exception as exc:
                errors.append({"page": page["title"], "category": category, "error": str(exc)})
        pages = {title: {"pageId": page["pageId"], "revisionId": page["revisionId"],
                         "revisionTimestamp": page["revisionTimestamp"]} for title, page in page_index.items()}
        manifest_pages.update(pages)
        entries.sort(key=lambda item: item["subject"].casefold())
        counts[category] = len(entries)
        sync_stats[category] = {"discovered": len(revisions), "reused": len(unchanged), "fetched": len(changed)}
        write_json(output_path, {
            "schemaVersion": 1, "importerVersion": IMPORTER_VERSION, "category": category,
            "language": "en", "retrievedAtUtc": retrieved,
            "targetGameVersion": args.target_game_version, "license": LICENSE, "entries": entries,
            "pages": pages,
        })
    manifest = {
        "schemaVersion": 1, "importerVersion": IMPORTER_VERSION, "source": "Stardew Valley Wiki",
        "sourceUrl": "https://stardewvalleywiki.com/", "apiUrl": API, "language": "en",
        "retrievedAtUtc": retrieved, "targetGameVersion": args.target_game_version,
        "license": LICENSE, "licenseUrl": "https://stardewvalleywiki.com/Stardew_Valley_Wiki:Copyrights",
        "attribution": "Contains structured facts derived from Stardew Valley Wiki contributors.",
        "counts": counts, "syncStats": sync_stats, "pages": manifest_pages, "errors": errors,
    }
    write_json(args.output / "manifest.json", manifest)
    print(json.dumps({"output": str(args.output.resolve()), "counts": counts, "errors": len(errors)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
