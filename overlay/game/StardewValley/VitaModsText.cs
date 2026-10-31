using System.Collections.Generic;

namespace StardewValley;

/// <summary>Small built-in menu translations; uses the game's current language fonts.</summary>
internal static class VitaModsText
{
	// title, title button, tractor, on, off, help. Allocated once, never per frame.
	private static readonly Dictionary<LocalizedContentManager.LanguageCode, string[]> Text = new Dictionary<LocalizedContentManager.LanguageCode, string[]>
	{
		{ LocalizedContentManager.LanguageCode.en, new[] { "Mods", "MODS", "Tractor", "ON", "OFF", "Buy a tractor kit from Robin and use it beside your horse.\nHold Square to use the selected tool in a 3x3 area." } },
		{ LocalizedContentManager.LanguageCode.tr, new[] { "Modlar", "MODLAR", "Traktör", "AÇIK", "KAPALI", "Robin'den traktör kiti al ve kendi atının yanında kullan.\nSeçili aleti 3x3 alanda kullanmak için Kare'yi basılı tut." } },
		{ LocalizedContentManager.LanguageCode.de, new[] { "Mods", "MODS", "Traktor", "AN", "AUS", "Kaufe bei Robin einen Traktor-Bausatz und benutze ihn neben deinem Pferd.\nHalte Quadrat gedrückt, um das gewählte Werkzeug im 3x3-Bereich zu nutzen." } },
		{ LocalizedContentManager.LanguageCode.es, new[] { "Mods", "MODS", "Tractor", "SÍ", "NO", "Compra un kit de tractor a Robin y úsalo junto a tu caballo.\nMantén Cuadrado para usar la herramienta seleccionada en un área de 3x3." } },
		{ LocalizedContentManager.LanguageCode.pt, new[] { "Mods", "MODS", "Trator", "SIM", "NÃO", "Compre um kit de trator da Robin e use-o ao lado do seu cavalo.\nSegure Quadrado para usar a ferramenta selecionada numa área de 3x3." } },
		{ LocalizedContentManager.LanguageCode.fr, new[] { "Mods", "MODS", "Tracteur", "OUI", "NON", "Achète un kit de tracteur chez Robin et utilise-le près de ton cheval.\nMaintiens Carré pour utiliser l'outil sélectionné sur une zone de 3x3." } },
		{ LocalizedContentManager.LanguageCode.it, new[] { "Mod", "MOD", "Trattore", "SÌ", "NO", "Compra un kit trattore da Robin e usalo accanto al tuo cavallo.\nTieni premuto Quadrato per usare l'attrezzo selezionato in un'area di 3x3." } },
		{ LocalizedContentManager.LanguageCode.hu, new[] { "Modok", "MODOK", "Traktor", "BE", "KI", "Vegyél traktorkészletet Robintól, és használd a saját lovad mellett.\nTartsd nyomva a Négyzet gombot a kijelölt eszköz 3x3-as használatához." } },
		{ LocalizedContentManager.LanguageCode.ru, new[] { "Моды", "МОДЫ", "Трактор", "ВКЛ", "ВЫКЛ", "Купите у Робин набор трактора и используйте его рядом с лошадью.\nУдерживайте Квадрат: инструмент работает на участке 3x3." } },
		{ LocalizedContentManager.LanguageCode.ja, new[] { "MOD", "MOD", "トラクター", "オン", "オフ", "ロビンからトラクターキットを買い、自分の馬のそばで使います。\n四角ボタンを押し続けると、選択中の道具を3x3の範囲で使えます。" } },
		{ LocalizedContentManager.LanguageCode.zh, new[] { "模组", "模组", "拖拉机", "开启", "关闭", "向罗宾购买拖拉机套件，在自己的马旁边使用。\n按住方块键，在3x3范围内使用当前选中的工具。" } },
		{ LocalizedContentManager.LanguageCode.ko, new[] { "모드", "모드", "트랙터", "켜짐", "꺼짐", "로빈에게 트랙터 키트를 사서 자신의 말 옆에서 사용하세요.\n네모 버튼을 누르고 있으면 선택한 도구를 3x3 범위에 사용합니다." } },
		{ LocalizedContentManager.LanguageCode.th, new[] { "ม็อด", "ม็อด", "รถแทรกเตอร์", "เปิด", "ปิด", "ซื้อชุดรถแทรกเตอร์จากโรบิน แล้วใช้ข้างม้าของคุณ\nกดปุ่มสี่เหลี่ยมค้างไว้เพื่อใช้เครื่องมือที่เลือกในพื้นที่ 3x3" } }
	};

	private static string[] Current => Text.TryGetValue(LocalizedContentManager.CurrentLanguageCode, out string[] value) ? value : Text[LocalizedContentManager.LanguageCode.en];
	public static string Title => Current[0];
	public static string Button => Current[1];
	public static string Help => VitaTractorText.Get(VitaTractorText.Help);
	public static string TractorLabel => Current[2] + ": " + Current[VitaTractorMod.Enabled ? 3 : 4];
	public static string Status(bool enabled) => Current[enabled ? 3 : 4];
}
