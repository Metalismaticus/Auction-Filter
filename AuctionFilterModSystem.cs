using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace AuctionFilter
{
	public class AuctionFilterModSystem : ModSystem
	{
		public const string HarmonyId = "metalismatic.auctionfilter";

		public static AuctionFilterModSystem Instance { get; private set; }

		private Harmony harmony;

		public ICoreClientAPI Capi;
		public AuctionFilterController Controller;

		public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

		public override double ExecuteOrder() => 0.5;

		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance = this;
			Capi = api;
			Controller = new AuctionFilterController(api);

			harmony = new Harmony(HarmonyId);
			harmony.PatchAll(typeof(AuctionFilterModSystem).Assembly);

			api.Logger.Notification("[AuctionFilter] loaded, Harmony patches applied");
		}

		public override void Dispose()
		{
			harmony?.UnpatchAll(HarmonyId);
			Controller?.Dispose();
			Controller = null;
			Capi = null;
			if (Instance == this) Instance = null;
		}
	}
}
