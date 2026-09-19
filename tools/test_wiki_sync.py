import unittest

import wiki_sync


class WikiSyncTests(unittest.TestCase):
    def page(self, title, text):
        return {"title": title, "text": text, "pageId": 1, "revisionId": 2, "revisionTimestamp": "2026-01-01T00:00:00Z"}

    def test_nested_infobox_crop(self):
        page = self.page("Parsnip", "{{Infobox|eng=Parsnip|seed={{Name|Parsnip Seeds}}|growth=4 days|season={{Season|Spring}}|sellprice=35}}")
        item = wiki_sync.parse_crop(page)
        self.assertEqual(item["facts"]["growthDays"], 4)
        self.assertEqual(item["facts"]["seasons"], ["Spring"])
        self.assertEqual(item["facts"]["seed"], "Parsnip Seeds")

    def test_fish_and_villager_templates(self):
        fish = self.page("Sunfish", "{{Infobox fish|location=River|time=6am – 7pm|season={{Season|Spring}} • {{Season|Summer}}|weather={{Weather inline|Sun}}|difficulty=30|price=30}}")
        self.assertEqual(wiki_sync.parse_fish(fish)["facts"]["seasons"], ["Spring", "Summer"])
        npc = self.page("Abigail", "{{Infobox villager|birthday={{Season|Fall|13}}|location=Pelican Town|marriage=Yes|favorites={{Name|Amethyst}}{{Name|Pumpkin}}}}\n{{GiftsByItem|love=Amethyst,Pumpkin|hate=Clay}}")
        parsed = wiki_sync.parse_villager(npc)
        self.assertEqual(parsed["facts"]["birthday"], {"season": "Fall", "day": 13})
        self.assertEqual(parsed["facts"]["giftPreferences"]["hate"], ["Clay"])

    def test_no_article_body_is_stored(self):
        page = self.page("Parsnip", "{{Infobox|growth=4|season=Spring}}\nCOPYRIGHTED LONG ARTICLE BODY")
        encoded = str(wiki_sync.parse_crop(page))
        self.assertNotIn("COPYRIGHTED LONG ARTICLE BODY", encoded)


if __name__ == "__main__":
    unittest.main()
