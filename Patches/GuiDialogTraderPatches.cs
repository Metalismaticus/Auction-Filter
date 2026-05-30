using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace AuctionFilter.Patches
{
	[HarmonyPatch(typeof(GuiDialogTrader), "Compose")]
	public static class Patch_GuiDialogTrader_Compose
	{
		private static readonly FieldInfo F_curTab        = AccessTools.Field(typeof(GuiDialogTrader), "curTab");
		private static readonly FieldInfo F_owningEntity  = AccessTools.Field(typeof(GuiDialogTrader), "owningEntity");
		private static readonly FieldInfo F_auctionSys    = AccessTools.Field(typeof(GuiDialogTrader), "auctionSys");
		private static readonly FieldInfo F_auctions      = AccessTools.Field(typeof(GuiDialogTrader), "auctions");
		private static readonly FieldInfo F_listElem      = AccessTools.Field(typeof(GuiDialogTrader), "listElem");
		private static readonly FieldInfo F_clipBounds    = AccessTools.Field(typeof(GuiDialogTrader), "clipBounds");
		private static readonly FieldInfo F_selected      = AccessTools.Field(typeof(GuiDialogTrader), "selectedElem");
		private static readonly MethodInfo M_updateScroll = AccessTools.Method(typeof(GuiDialogTrader), "updateScrollbarBounds");

		public static bool Prefix(GuiDialogTrader __instance)
		{
			var sys = AuctionFilterModSystem.Instance;
			if (sys == null || sys.Controller == null) return true;

			int tab = (int)(F_curTab?.GetValue(__instance) ?? 0);
			if (tab != 1 && tab != 2) return true; // vanilla for "Local goods"

			try
			{
				ComposeAuctionTab(__instance, tab, sys);
				return false;
			}
			catch (Exception e)
			{
				sys.Capi.Logger.Error("[AuctionFilter] inline compose failed, falling back to vanilla: {0}", e);
				return true;
			}
		}

		private static void ComposeAuctionTab(GuiDialogTrader trader, int tab, AuctionFilterModSystem mod)
		{
			var capi = mod.Capi;
			var ctrl = mod.Controller;
			ctrl.OnTraderTabChanged(trader);

			var owningEntity = (EntityAgent)F_owningEntity.GetValue(trader);
			var auctionSys = (ModSystemAuction)F_auctionSys.GetValue(trader);
			bool auctionHouseEnabled = capi.World.Config.GetBool("auctionHouse", true);

			GuiTab[] tabs = auctionHouseEnabled
				? new[] {
					new GuiTab { Name = Lang.Get("Local goods"),  DataInt = 0 },
					new GuiTab { Name = Lang.Get("Auction house"), DataInt = 1 },
					new GuiTab { Name = Lang.Get("Your Auctions"), DataInt = 2 },
				}
				: new[] { new GuiTab { Name = Lang.Get("Local goods"), DataInt = 0 } };

			ElementBounds tabBounds = ElementBounds.Fixed(0, -24, 400, 25);
			CairoFont tabFont = CairoFont.WhiteDetailText();

			ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
			bgBounds.BothSizing = ElementSizing.FitToChildren;

			ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
				.WithAlignment(EnumDialogArea.RightMiddle)
				.WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

			ElementBounds leftBtn  = ElementBounds.Fixed(EnumDialogArea.LeftFixed,  0, 0, 0, 0).WithFixedPadding(8, 5);
			ElementBounds rightBtn = ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, 0, 0, 0).WithFixedPadding(8, 5);

			string title = (tab > 0)
				? Lang.Get("tradertabtitle-" + tab)
				: Lang.GetMatching("tradingwindow-" + ((Vintagestory.API.Common.Entities.Entity)owningEntity).Code.Path,
					((Vintagestory.API.Common.Entities.Entity)owningEntity).GetBehavior<EntityBehaviorNameTag>().DisplayName);

			// Filter strip layout - sits at the top of the dialog body, above the auction list.
			const double stripWidth = 700.0;
			const double stripHeight = 32.0;
			const double stripGap = 8.0;
			const double stripTop = 25.0;

			ElementBounds searchBnd   = ElementBounds.Fixed(0,   stripTop, 240, stripHeight);
			ElementBounds catBnd      = ElementBounds.Fixed(248, stripTop, 200, stripHeight);
			ElementBounds minBnd      = ElementBounds.Fixed(456, stripTop,  90, stripHeight);
			ElementBounds maxBnd      = ElementBounds.Fixed(554, stripTop,  90, stripHeight);
			ElementBounds resetBnd    = ElementBounds.Fixed(652, stripTop,  48, stripHeight);

			// Auction list bounds, pushed down to make room for the filter strip.
			double listY = stripTop + stripHeight + stripGap;
			double listH = 377.0;
			ElementBounds listBnd  = ElementBounds.Fixed(0, listY, stripWidth, listH);
			ElementBounds clipBnd  = listBnd.ForkBoundingParent(0, 0, 0, 0);
			ElementBounds insetBnd = listBnd.FlatCopy().FixedGrow(3.0).WithFixedOffset(0, 0);
			ElementBounds scrollBnd = insetBnd.CopyOffsetedSibling(3.0 + listBnd.fixedWidth + 7.0, 0, 0, 0).WithFixedWidth(20);

			F_clipBounds.SetValue(trader, clipBnd);

			// Categories
			var cats = AuctionFilterState.CollectCategories(capi);
			var catValues = new List<string>(cats.Count + 1) { "" };
			catValues.AddRange(cats);
			var catNames = new string[catValues.Count];
			catNames[0] = Lang.Get("auctionfilter:cat-all");
			for (int i = 1; i < catValues.Count; i++) catNames[i] = LocalizeCategory(catValues[i]);

			// Reflected private callbacks of the trader
			Action onTitleBarClose = MakeAction(trader, "OnTitleBarClose");
			Action<int> onTabClicked = MakeActionInt(trader, "OnTabClicked");
			Action<float> onScroll = MakeActionFloat(trader, "OnNewScrollbarValue");
			ActionConsumable onByeClicked = MakeActionConsumable(trader, "OnByeClicked");
			ActionConsumable onBuy = MakeActionConsumable(trader, "OnBuyAuctionClicked");
			ActionConsumable onPlace = MakeActionConsumable(trader, "OnCreateAuction");
			ActionConsumable onCancel = MakeActionConsumable(trader, "OnCancelAuction");
			ActionConsumable onCollect = MakeActionConsumable(trader, "OnCollectFunds");
			ActionConsumable onRetrieve = MakeActionConsumable(trader, "OnRetrieveItems");
			OnRequireCell<Auction> createCell = (OnRequireCell<Auction>)Delegate.CreateDelegate(
				typeof(OnRequireCell<Auction>), trader, AccessTools.Method(typeof(GuiDialogTrader), "createCell"));
			Action<int> didClickAuctionElem = MakeActionInt(trader, "didClickAuctionElem");

			// Initial filter snapshot
			var sourceList = (tab == 1) ? auctionSys.activeAuctions : auctionSys.ownAuctions;
			var filtered = ctrl.State.Apply(sourceList, capi);

			var compo = capi.Gui.CreateCompo("traderdialog-" + ((Vintagestory.API.Common.Entities.Entity)owningEntity).EntityId, dialogBounds)
				.AddShadedDialogBG(bgBounds, true, 5.0, 0.75f)
				.AddDialogTitleBar(title, onTitleBarClose)
				.AddHorizontalTabs(tabs, tabBounds, onTabClicked, tabFont, tabFont.Clone().WithColor(GuiStyle.ActiveButtonTextColor), "tabs")
				.BeginChildElements(bgBounds);

			compo.GetHorizontalTabs("tabs").activeElement = tab;

			compo
				.AddTextInput(searchBnd,
					(Action<string>)(t => { ctrl.State.SearchText = t ?? ""; RefreshList(trader, ctrl, capi, tab); }),
					CairoFont.WhiteSmallText(), "af-search")
				.AddDropDown(catValues.ToArray(), catNames,
					Math.Max(0, catValues.IndexOf(ctrl.State.CategoryTab ?? "")),
					(SelectionChangedDelegate)((code, sel) => { ctrl.State.CategoryTab = code ?? ""; RefreshList(trader, ctrl, capi, tab); }),
					catBnd, CairoFont.WhiteSmallText(), "af-category")
				.AddNumberInput(minBnd,
					(Action<string>)(t => { ctrl.State.MinPrice = ParsePrice(t); RefreshList(trader, ctrl, capi, tab); }),
					CairoFont.WhiteSmallText(), "af-min")
				.AddNumberInput(maxBnd,
					(Action<string>)(t => { ctrl.State.MaxPrice = ParsePrice(t); RefreshList(trader, ctrl, capi, tab); }),
					CairoFont.WhiteSmallText(), "af-max")
				.AddSmallButton(Lang.Get("auctionfilter:reset-short"),
					(ActionConsumable)(() => { ctrl.ResetFilter(); RestoreFilterControls(trader, ctrl); RefreshList(trader, ctrl, capi, tab); return true; }),
					resetBnd, EnumButtonStyle.Normal, "af-reset")
				.BeginClip(clipBnd)
					.AddInset(insetBnd, 3, 0.85f)
					.AddCellList<Auction>(listBnd, createCell, filtered, "stacklist")
				.EndClip()
				.AddVerticalScrollbar(onScroll, scrollBnd, "scrollbar");

			if (tab == 1)
			{
				compo.AddSmallButton(Lang.Get("Goodbye!"), onByeClicked, leftBtn.FixedUnder(clipBnd, 20), EnumButtonStyle.Normal);
				compo.AddSmallButton(Lang.Get("Buy"), onBuy, rightBtn.FixedUnder(clipBnd, 20), EnumButtonStyle.Normal, "buyauction");
			}
			else // tab == 2
			{
				ElementBounds cancelLikeBnd = ElementBounds.Fixed(EnumDialogArea.RightFixed, 0, 0, 0, 0).WithFixedPadding(8, 5);
				var placeText = Lang.Get("Place Auction");
				var extents = CairoFont.ButtonText().GetTextExtents(placeText);
				double placeWidth = extents.Width / RuntimeEnv.GUIScale;

				compo.AddSmallButton(Lang.Get("Goodbye!"), onByeClicked, leftBtn.FixedUnder(clipBnd, 20), EnumButtonStyle.Normal);
				compo.AddSmallButton(placeText, onPlace, rightBtn.FixedUnder(clipBnd, 20), EnumButtonStyle.Normal, "placeAuction");
				compo.AddSmallButton(Lang.Get("Cancel Auction"), onCancel,
					cancelLikeBnd.FlatCopy().FixedUnder(clipBnd, 20).WithFixedAlignmentOffset(-placeWidth, 0),
					EnumButtonStyle.Normal, "cancelAuction");
				compo.AddSmallButton(Lang.Get("Collect Funds"), onCollect,
					cancelLikeBnd.FlatCopy().FixedUnder(clipBnd, 20).WithFixedAlignmentOffset(-placeWidth, 0),
					EnumButtonStyle.Normal, "collectFunds");
				compo.AddSmallButton(Lang.Get("Retrieve Items"), onRetrieve,
					cancelLikeBnd.FixedUnder(clipBnd, 20).WithFixedAlignmentOffset(-placeWidth, 0),
					EnumButtonStyle.Normal, "retrieveItems");
			}

			F_selected.SetValue(trader, null);
			F_auctions.SetValue(trader, filtered);

			var listElem = compo.GetCellList<Auction>("stacklist");
			listElem.BeforeCalcBounds();
			listElem.UnscaledCellVerPadding = 0;
			listElem.unscaledCellSpacing = 5;
			F_listElem.SetValue(trader, listElem);

			compo.EndChildElements().Compose();
			trader.SingleComposer = compo;

			// Restore field values into freshly-composed controls
			RestoreFilterControls(trader, ctrl);
			SetPlaceholders(trader);

			M_updateScroll?.Invoke(trader, null);
			didClickAuctionElem?.Invoke(-1);

			// Rewire the auction system's update callback so server-pushed updates re-apply our filter
			auctionSys.OnCellUpdateClient = ctrl.RefilterCallback;
		}

		private static void RefreshList(GuiDialogTrader trader, AuctionFilterController ctrl, ICoreClientAPI capi, int tab)
		{
			ctrl.ApplyFilterToTrader();
		}

		private static void RestoreFilterControls(GuiDialogTrader trader, AuctionFilterController ctrl)
		{
			var compo = trader.SingleComposer;
			if (compo == null) return;

			var s = compo.GetTextInput("af-search");
			s?.SetValue(ctrl.State.SearchText ?? "");

			var c = compo.GetDropDown("af-category");
			c?.SetSelectedValue(ctrl.State.CategoryTab ?? "");

			var mn = compo.GetNumberInput("af-min");
			mn?.SetValue(ctrl.State.MinPrice.HasValue ? ctrl.State.MinPrice.Value.ToString() : "");

			var mx = compo.GetNumberInput("af-max");
			mx?.SetValue(ctrl.State.MaxPrice.HasValue ? ctrl.State.MaxPrice.Value.ToString() : "");
		}

		private static void SetPlaceholders(GuiDialogTrader trader)
		{
			var compo = trader.SingleComposer;
			compo?.GetTextInput("af-search")?.SetPlaceHolderText(Lang.Get("auctionfilter:search-placeholder"));
		}

		private static int? ParsePrice(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;
			if (int.TryParse(text.Trim(), out int v) && v >= 0) return v;
			return null;
		}

		private static string LocalizeCategory(string code)
		{
			string langKey = "tabname-" + code;
			string translated = Lang.Get(langKey);
			if (translated != langKey) return translated;
			translated = Lang.Get(code);
			if (translated != code) return translated;
			return string.IsNullOrEmpty(code) ? code : char.ToUpperInvariant(code[0]) + code.Substring(1);
		}

		private static Action MakeAction(object target, string name)
			=> (Action)Delegate.CreateDelegate(typeof(Action), target, AccessTools.Method(target.GetType(), name));

		private static Action<int> MakeActionInt(object target, string name)
			=> (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), target, AccessTools.Method(target.GetType(), name));

		private static Action<float> MakeActionFloat(object target, string name)
			=> (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), target, AccessTools.Method(target.GetType(), name));

		private static ActionConsumable MakeActionConsumable(object target, string name)
			=> (ActionConsumable)Delegate.CreateDelegate(typeof(ActionConsumable), target, AccessTools.Method(target.GetType(), name));
	}

	[HarmonyPatch(typeof(GuiDialogTrader), "OnGuiOpened")]
	public static class Patch_GuiDialogTrader_OnGuiOpened
	{
		public static void Postfix(GuiDialogTrader __instance)
		{
			AuctionFilterModSystem.Instance?.Controller?.OnTraderOpened(__instance);
		}
	}

	[HarmonyPatch(typeof(GuiDialogTrader), "OnGuiClosed")]
	public static class Patch_GuiDialogTrader_OnGuiClosed
	{
		public static void Postfix(GuiDialogTrader __instance)
		{
			AuctionFilterModSystem.Instance?.Controller?.OnTraderClosed(__instance);
		}
	}
}
