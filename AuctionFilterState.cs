using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AuctionFilter
{
	public class AuctionFilterState
	{
		public string SearchText = "";
		public string CategoryTab = ""; // "" = all
		public int? MinPrice = null;
		public int? MaxPrice = null;

		public bool IsActive => !string.IsNullOrEmpty(SearchText)
								|| !string.IsNullOrEmpty(CategoryTab)
								|| MinPrice.HasValue
								|| MaxPrice.HasValue;

		public List<Auction> Apply(List<Auction> source, ICoreClientAPI capi)
		{
			if (source == null) return new List<Auction>();
			if (!IsActive)
			{
				return new List<Auction>(source);
			}

			string needle = (SearchText ?? "").Trim();
			bool hasNeedle = needle.Length > 0;
			string needleLower = needle.ToLowerInvariant();

			string cat = CategoryTab ?? "";
			bool hasCat = cat.Length > 0;

			int min = MinPrice ?? int.MinValue;
			int max = MaxPrice ?? int.MaxValue;

			var result = new List<Auction>(source.Count);
			for (int i = 0; i < source.Count; i++)
			{
				var a = source[i];
				if (a == null || a.ItemStack == null) continue;

				if (a.Price < min || a.Price > max) continue;

				if (hasNeedle)
				{
					string name = a.ItemStack.GetName() ?? "";
					if (name.IndexOf(needleLower, StringComparison.OrdinalIgnoreCase) < 0) continue;
				}

				if (hasCat)
				{
					var col = a.ItemStack.Collectible;
					string[] tabs = col?.CreativeInventoryTabs;
					if (tabs == null || Array.IndexOf(tabs, cat) < 0) continue;
				}

				result.Add(a);
			}
			return result;
		}

		public static List<string> CollectCategories(ICoreClientAPI capi)
		{
			var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			var blocks = capi.World.Blocks;
			for (int i = 0; i < blocks.Count; i++)
			{
				var b = blocks[i];
				if (b == null) continue;
				var tabs = b.CreativeInventoryTabs;
				if (tabs == null) continue;
				for (int j = 0; j < tabs.Length; j++)
				{
					if (!string.IsNullOrEmpty(tabs[j])) set.Add(tabs[j]);
				}
			}
			var items = capi.World.Items;
			for (int i = 0; i < items.Count; i++)
			{
				var it = items[i];
				if (it == null) continue;
				var tabs = it.CreativeInventoryTabs;
				if (tabs == null) continue;
				for (int j = 0; j < tabs.Length; j++)
				{
					if (!string.IsNullOrEmpty(tabs[j])) set.Add(tabs[j]);
				}
			}
			return new List<string>(set);
		}
	}
}
