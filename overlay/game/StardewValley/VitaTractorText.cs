using System.Collections.Generic;

namespace StardewValley;

/// <summary>Local display text only. No translated strings are written to items or saves.</summary>
internal static class VitaTractorText
{
	public const int KitName = 0, Description = 1, Help = 2, EnableFirst = 3, NearHorse = 4, MissingKit = 5, ToHorse = 6, ToTractor = 7;
	private static readonly Dictionary<LocalizedContentManager.LanguageCode, string[]> Text = new Dictionary<LocalizedContentManager.LanguageCode, string[]>
	{
		{ LocalizedContentManager.LanguageCode.en, new[] {
			"Tractor kit", "Wear the ring or carry the kit. Near or riding your own horse, hold L and R and press Down to switch horse/tractor. Hold Square on the tractor: selected tool in a 3x3 area.",
			"Buy a tractor kit ring from Robin. Wear it or keep it in your backpack. Near or riding your own horse: L + R + Down switches horse/tractor. Hold Square to work a 3x3 area.",
			"Enable Tractor in the Mods menu first.", "Use near or while riding your own horse.", "Carry or wear a tractor kit first.", "Tractor changed back to horse.", "Horse changed to tractor!" } },
		{ LocalizedContentManager.LanguageCode.tr, new[] {
			"Traktör kiti", "Yüzüğü tak veya kiti çantanda taşı. Kendi atının yanında ya da üstündeyken L ve R'yi tutup Aşağı'ya bas: at/traktör dönüşümü. Traktörde Kare'yi tut: seçili alet 3x3 alanda çalışır.",
			"Robin'den traktör kiti yüzüğü al. Tak veya çantanda taşı. Kendi atının yanında ya da üstündeyken L + R + Aşağı: at/traktör dönüşümü. Kare'yi tutarak 3x3 alanda çalış.",
			"Önce Modlar menüsünden Traktör'ü aç.", "Kendi atının yanında veya üstündeyken kullan.", "Önce traktör kitini çantana al veya tak.", "Traktör tekrar ata dönüştü.", "At traktöre dönüştü!" } },
		{ LocalizedContentManager.LanguageCode.de, new[] {
			"Traktorset", "Trage den Ring oder das Set im Rucksack. Neben oder auf deinem Pferd L und R halten und Unten drücken: Pferd/Traktor wechseln. Auf dem Traktor Quadrat halten: Werkzeug auf 3x3 Feldern.",
			"Kaufe den Traktorring bei Robin. Trage ihn oder bewahre ihn im Rucksack auf. Neben oder auf deinem Pferd: L + R + Unten wechselt Pferd/Traktor. Quadrat halten: 3x3 Felder bearbeiten.",
			"Aktiviere zuerst Traktor im Mods-Menü.", "Neben oder auf deinem eigenen Pferd benutzen.", "Trage zuerst ein Traktorset bei dir.", "Traktor wieder in Pferd verwandelt.", "Pferd in Traktor verwandelt!" } },
		{ LocalizedContentManager.LanguageCode.es, new[] {
			"Kit de tractor", "Equipa el anillo o lleva el kit. Junto a tu caballo o montándolo, mantén L y R y pulsa Abajo: caballo/tractor. En el tractor, mantén Cuadrado: herramienta en un área de 3x3.",
			"Compra el anillo de tractor a Robin. Equípalo o llévalo en la mochila. Junto a tu caballo o montándolo: L + R + Abajo cambia caballo/tractor. Mantén Cuadrado para trabajar en 3x3.",
			"Activa Tractor en el menú de mods primero.", "Úsalo junto a tu caballo o montándolo.", "Lleva o equipa un kit de tractor primero.", "El tractor vuelve a ser un caballo.", "¡Caballo convertido en tractor!" } },
		{ LocalizedContentManager.LanguageCode.pt, new[] {
			"Kit de trator", "Equipe o anel ou leve o kit. Perto do seu cavalo ou montado nele, segure L e R e aperte Baixo: cavalo/trator. No trator, segure Quadrado: ferramenta numa área de 3x3.",
			"Compre o anel de trator da Robin. Equipe ou guarde na mochila. Perto do seu cavalo ou montado nele: L + R + Baixo troca cavalo/trator. Segure Quadrado para trabalhar em 3x3.",
			"Ative Trator no menu de mods primeiro.", "Use perto do seu cavalo ou montado nele.", "Leve ou equipe um kit de trator primeiro.", "O trator voltou a ser um cavalo.", "Cavalo transformado em trator!" } },
		{ LocalizedContentManager.LanguageCode.fr, new[] {
			"Kit de tracteur", "Porte l'anneau ou garde le kit dans le sac. Près de ton cheval ou à cheval, maintiens L et R et appuie sur Bas : cheval/tracteur. Sur le tracteur, maintiens Carré : outil sur 3x3 cases.",
			"Achète l'anneau de tracteur chez Robin. Porte-le ou garde-le dans le sac. Près de ton cheval ou à cheval : L + R + Bas change cheval/tracteur. Maintiens Carré pour travailler sur 3x3 cases.",
			"Active d'abord Tracteur dans le menu des mods.", "Utilise-le près de ton cheval ou à cheval.", "Porte ou emporte d'abord un kit de tracteur.", "Le tracteur est redevenu un cheval.", "Cheval transformé en tracteur !" } },
		{ LocalizedContentManager.LanguageCode.it, new[] {
			"Kit trattore", "Indossa l'anello o porta il kit. Vicino al tuo cavallo o in sella, tieni L e R e premi Giù: cavallo/trattore. Sul trattore, tieni Quadrato: attrezzo su un'area di 3x3.",
			"Compra l'anello trattore da Robin. Indossalo o tienilo nello zaino. Vicino al tuo cavallo o in sella: L + R + Giù cambia cavallo/trattore. Tieni Quadrato per lavorare su un'area di 3x3.",
			"Attiva prima Trattore nel menu delle mod.", "Usalo vicino al tuo cavallo o in sella.", "Porta o indossa prima un kit trattore.", "Il trattore è tornato un cavallo.", "Cavallo trasformato in trattore!" } },
		{ LocalizedContentManager.LanguageCode.hu, new[] {
			"Traktorkészlet", "Viseld a gyürüt, vagy vidd a készletet. Saját lovad mellett vagy rajta ülve tartsd az L és R gombot, majd nyomj Le gombot: ló/traktor. Traktoron tartsd a Négyzetet: eszköz 3x3 területen.",
			"Vegyél traktorgyürüt Robintól. Viseld, vagy tartsd a hátizsákban. Saját lovad mellett vagy rajta ülve: L + R + Le vált ló és traktor között. Tartsd a Négyzetet a 3x3-as munkához.",
			"Előbb kapcsold be a Traktort a Modok menüben.", "Saját lovad mellett vagy rajta ülve használd.", "Előbb vigyél vagy viselj traktorkészletet.", "A traktor újra ló lett.", "A ló traktorrá alakult!" } },
		{ LocalizedContentManager.LanguageCode.ru, new[] {
			"Набор трактора", "Наденьте кольцо или носите набор в рюкзаке. Рядом со своей лошадью или верхом зажмите L и R, нажмите Вниз: лошадь/трактор. На тракторе держите Квадрат: инструмент на участке 3x3.",
			"Купите кольцо трактора у Робин. Наденьте или положите в рюкзак. Рядом со своей лошадью или верхом зажмите L и R, нажмите Вниз: лошадь/трактор. Держите Квадрат: работа на участке 3x3.",
			"Сначала включите трактор в меню модов.", "Используйте рядом со своей лошадью или верхом.", "Сначала возьмите или наденьте набор трактора.", "Трактор снова стал лошадью.", "Лошадь превращена в трактор!" } },
		{ LocalizedContentManager.LanguageCode.ja, new[] {
			"トラクターキット", "指輪を装備するか、キットをかばんに入れます。自分の馬のそばか騎乗中にLとRを押しながら下：馬とトラクターを切り替えます。トラクターで四角ボタン長押し：選択中の道具を3x3の範囲で使います。",
			"ロビンからトラクターの指輪を購入し、装備するかかばんに入れます。自分の馬のそばか騎乗中にLとRを押しながら下：馬とトラクターを切り替えます。四角ボタン長押しで3x3の範囲を作業します。",
			"先にMODメニューでトラクターをオンにしてください。", "自分の馬のそばか騎乗中に使ってください。", "先にトラクターキットを持つか装備してください。", "トラクターを馬に戻しました。", "馬をトラクターに変えました！" } },
		{ LocalizedContentManager.LanguageCode.zh, new[] {
			"拖拉机套件", "装备戒指或将套件放在背包中。在自己的马旁或骑马时，按住L和R再按向下键：切换马与拖拉机。驾驶拖拉机时按住方块键：在3x3范围内使用当前工具。",
			"向罗宾购买拖拉机戒指，装备或放在背包中。在自己的马旁或骑马时，按住L和R再按向下键：切换马与拖拉机。按住方块键可在3x3范围内作业。",
			"请先在模组菜单中开启拖拉机。", "请在自己的马旁或骑马时使用。", "请先携带或装备拖拉机套件。", "拖拉机已变回马。", "马已变成拖拉机！" } },
		{ LocalizedContentManager.LanguageCode.ko, new[] {
			"트랙터 키트", "반지를 착용하거나 키트를 소지하세요. 자기 말 옆이나 탄 상태에서 L과 R을 누른 채 아래: 말/트랙터 전환. 트랙터에서 네모 길게: 선택 도구로 3x3 작업.",
			"로빈에게 트랙터 반지를 사서 착용하거나 가방에 넣으세요. 자기 말 옆이나 말을 탄 상태에서 L과 R을 누른 채 아래 방향키: 말과 트랙터 전환. 네모 버튼을 길게 눌러 3x3 범위에서 작업하세요.",
			"먼저 모드 메뉴에서 트랙터를 켜세요.", "자기 말 옆이나 말을 탄 상태에서 사용하세요.", "먼저 트랙터 키트를 소지하거나 착용하세요.", "트랙터가 다시 말로 바뀌었습니다.", "말이 트랙터로 바뀌었습니다!" } },
		{ LocalizedContentManager.LanguageCode.th, new[] {
			"ชุดรถแทรกเตอร์", "สวมแหวนหรือพกชุดไว้ในกระเป๋า อยู่ข้างม้าของคุณหรือขี่ม้า กด L และ R ค้างแล้วกดลงเพื่อสลับม้ากับรถแทรกเตอร์ บนรถกดสี่เหลี่ยมค้างเพื่อใช้เครื่องมือในพื้นที่ 3x3",
			"ซื้อแหวนรถแทรกเตอร์จากโรบิน สวมหรือเก็บในกระเป๋า อยู่ข้างม้าของคุณหรือขี่ม้า กด L และ R ค้างแล้วกดลงเพื่อสลับม้ากับรถแทรกเตอร์ กดสี่เหลี่ยมค้างเพื่อทำงานในพื้นที่ 3x3",
			"เปิดรถแทรกเตอร์ในเมนูม็อดก่อน", "ใช้ข้างม้าของคุณหรือขณะขี่ม้า", "พกหรือสวมชุดรถแทรกเตอร์ก่อน", "รถแทรกเตอร์กลับเป็นม้าแล้ว", "ม้ากลายเป็นรถแทรกเตอร์แล้ว!" } }
	};
	public static string Get(int index) => (Text.TryGetValue(LocalizedContentManager.CurrentLanguageCode, out string[] value) ? value : Text[LocalizedContentManager.LanguageCode.en])[index];
}
