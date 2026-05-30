using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AuctionFilter
{
	public class AuctionFilterController : IDisposable
	{
		private readonly ICoreClientAPI capi;
		public readonly AuctionFilterState State = new AuctionFilterState();

		public GuiDialogTrader CurrentTrader;
		public readonly Action RefilterCallback;

		private static readonly FieldInfo F_auctions   = AccessTools.Field(typeof(GuiDialogTrader), "auctions");
		private static readonly FieldInfo F_listElem   = AccessTools.Field(typeof(GuiDialogTrader), "listElem");
		private static readonly FieldInfo F_curTab     = AccessTools.Field(typeof(GuiDialogTrader), "curTab");
		private static readonly FieldInfo F_auctionSys = AccessTools.Field(typeof(GuiDialogTrader), "auctionSys");
		private static readonly FieldInfo F_selected   = AccessTools.Field(typeof(GuiDialogTrader), "selectedElem");
		private static readonly MethodInfo M_updateScroll = AccessTools.Method(typeof(GuiDialogTrader), "updateScrollbarBounds");

		public AuctionFilterController(ICoreClientAPI capi)
		{
			this.capi = capi;
			RefilterCallback = ApplyFilterToTrader;
		}

		public void OnTraderOpened(GuiDialogTrader trader) => CurrentTrader = trader;
		public void OnTraderTabChanged(GuiDialogTrader trader) => CurrentTrader = trader;

		public void OnTraderClosed(GuiDialogTrader trader)
		{
			if (CurrentTrader == trader) CurrentTrader = null;
		}

		public int GetCurTab()
		{
			if (CurrentTrader == null || F_curTab == null) return 0;
			object v = F_curTab.GetValue(CurrentTrader);
			return v is int i ? i : 0;
		}

		public ModSystemAuction GetAuctionSys()
		{
			if (CurrentTrader == null || F_auctionSys == null) return null;
			return F_auctionSys.GetValue(CurrentTrader) as ModSystemAuction;
		}

		public List<Auction> GetSourceForCurrentTab()
		{
			var sys = GetAuctionSys();
			if (sys == null) return null;
			int tab = GetCurTab();
			if (tab == 1) return sys.activeAuctions;
			if (tab == 2) return sys.ownAuctions;
			return null;
		}

		public void ResetFilter()
		{
			State.SearchText = "";
			State.CategoryTab = "";
			State.MinPrice = null;
			State.MaxPrice = null;
		}

		public void ApplyFilterToTrader()
		{
			if (CurrentTrader == null) return;
			int tab = GetCurTab();
			if (tab != 1 && tab != 2) return;

			var src = GetSourceForCurrentTab();
			if (src == null) return;

			var filtered = State.Apply(src, capi);

			F_auctions?.SetValue(CurrentTrader, filtered);
			F_selected?.SetValue(CurrentTrader, null);

			var listElem = F_listElem?.GetValue(CurrentTrader) as GuiElementCellList<Auction>;
			if (listElem == null) return;

			listElem.ReloadCells(filtered);
			M_updateScroll?.Invoke(CurrentTrader, null);
		}

		public void Dispose()
		{
			CurrentTrader = null;
		}
	}
}
