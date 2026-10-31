using System.Collections.Generic;

namespace StardewValley;

/// <summary>Chest helper strings (Mods menu, L + R + up in an open chest, chest search). Allocated once, never per frame.</summary>
internal static class VitaChestText
{
	public const int Label = 0, Help = 1, Stacked = 2, NothingToStack = 3, Search = 4, SearchHint = 5, OpenChestFirst = 6;

	private static readonly Dictionary<LocalizedContentManager.LanguageCode, string[]> Text = new Dictionary<LocalizedContentManager.LanguageCode, string[]>
	{
		{ LocalizedContentManager.LanguageCode.en, new[] { "Chest Helper", "In an open chest, L + R + Up puts backpack items the chest already holds into it. Triangle in a chest: search.", "Put into the chest: {0}", "No backpack items match this chest", "Search", "Triangle: Search", "Open a chest first" } },
		{ LocalizedContentManager.LanguageCode.tr, new[] { "Sandık Yardımcısı", "Sandık açıkken L + R + Yukarı: çantadaki eşyalardan sandıkta zaten olanları ona koyar. Sandıkta Üçgen: arama.", "Sandığa kondu: {0}", "Çantada bu sandıkla eşleşen eşya yok", "Ara", "Üçgen: Ara", "Önce bir sandık aç" } },
		{ LocalizedContentManager.LanguageCode.de, new[] { "Truhenhelfer", "In einer offenen Truhe legt L + R + Oben Rucksackgegenstände hinein, die sie schon enthält. Dreieck in einer Truhe: Suche.", "In die Truhe gelegt: {0}", "Keine passenden Gegenstände für diese Truhe", "Suche", "Dreieck: Suche", "Öffne zuerst eine Truhe" } },
		{ LocalizedContentManager.LanguageCode.es, new[] { "Ayudante de cofres", "Con un cofre abierto, L + R + Arriba guarda en él los objetos de la mochila que ya contiene. Triángulo en un cofre: buscar.", "Guardado en el cofre: {0}", "Ningún objeto de la mochila coincide con este cofre", "Buscar", "Triángulo: Buscar", "Abre primero un cofre" } },
		{ LocalizedContentManager.LanguageCode.pt, new[] { "Ajudante de baús", "Com um baú aberto, L + R + Cima guarda nele os itens da mochila que ele já tem. Triângulo num baú: buscar.", "Guardado no baú: {0}", "Nenhum item da mochila combina com este baú", "Buscar", "Triângulo: Buscar", "Abra um baú primeiro" } },
		{ LocalizedContentManager.LanguageCode.fr, new[] { "Aide aux coffres", "Dans un coffre ouvert, L + R + Haut y range les objets du sac qu'il contient déjà. Triangle dans un coffre : recherche.", "Rangé dans le coffre : {0}", "Aucun objet du sac ne correspond à ce coffre", "Recherche", "Triangle : Recherche", "Ouvre d'abord un coffre" } },
		{ LocalizedContentManager.LanguageCode.it, new[] { "Aiuto bauli", "In un baule aperto, L + R + Su vi mette gli oggetti dello zaino che contiene già. Triangolo in un baule: cerca.", "Messi nel baule: {0}", "Nessun oggetto dello zaino corrisponde a questo baule", "Cerca", "Triangolo: Cerca", "Apri prima un baule" } },
		{ LocalizedContentManager.LanguageCode.hu, new[] { "Ládasegéd", "Nyitott ládában az L + R + Fel a hátizsák azon tárgyait teszi bele, amelyek már benne vannak. Háromszög ládában: keresés.", "Ládába rakva: {0}", "Nincs a ládához illő tárgy a hátizsákban", "Keresés", "Háromszög: Keresés", "Előbb nyiss ki egy ládát" } },
		{ LocalizedContentManager.LanguageCode.ru, new[] { "Помощник сундуков", "В открытом сундуке, удерживая L и R, нажмите Вверх: вещи из рюкзака, которые уже есть в сундуке, лягут в него. Треугольник в сундуке: поиск.", "Положено в сундук: {0}", "В рюкзаке нет вещей для этого сундука", "Поиск", "Треугольник: Поиск", "Сначала откройте сундук" } },
		{ LocalizedContentManager.LanguageCode.ja, new[] { "チェスト補助", "チェストを開いてL + R + 上：チェストに入っているのと同じアイテムを、かばんから入れます。チェスト内で三角ボタン：検索。", "チェストに収納：{0}", "このチェストと同じアイテムがかばんにありません", "検索", "三角ボタン：検索", "先にチェストを開いてください" } },
		{ LocalizedContentManager.LanguageCode.zh, new[] { "箱子助手", "打开箱子时按 L + R + 上：把箱子里已有的同类物品从背包放进去。在箱子中按三角键：搜索。", "已放入箱子：{0}", "背包中没有与此箱子相同的物品", "搜索", "三角键：搜索", "请先打开箱子" } },
		{ LocalizedContentManager.LanguageCode.ko, new[] { "상자 도우미", "상자를 연 상태에서 L + R + 위: 상자에 이미 있는 아이템을 가방에서 넣습니다. 상자에서 세모 버튼: 검색.", "상자에 넣음: {0}", "이 상자와 같은 아이템이 가방에 없습니다", "검색", "세모 버튼: 검색", "먼저 상자를 여세요" } },
		{ LocalizedContentManager.LanguageCode.th, new[] { "ตัวช่วยหีบ", "เปิดหีบแล้วกด L + R + ขึ้น: ใส่ของในกระเป๋าที่หีบมีอยู่แล้วลงหีบ กดสามเหลี่ยมในหีบเพื่อค้นหา", "ใส่ลงหีบแล้ว: {0}", "ไม่มีของในกระเป๋าที่ตรงกับหีบนี้", "ค้นหา", "สามเหลี่ยม: ค้นหา", "เปิดหีบก่อน" } }
	};

	public static string Get(int index) => (Text.TryGetValue(LocalizedContentManager.CurrentLanguageCode, out string[] value) ? value : Text[LocalizedContentManager.LanguageCode.en])[index];
}

/// <summary>Passable Crops menu strings. Allocated once, never per frame.</summary>
internal static class VitaPassableText
{
	public const int Title = 0, RowHelp = 1, Mod = 2, Crops = 3, Scarecrows = 4, Sprinklers = 5, Forage = 6, TeaBushes = 7, Trees = 8, FruitTrees = 9, Weeds = 10, SlowDown = 11, Footer = 12;

	private static readonly Dictionary<LocalizedContentManager.LanguageCode, string[]> Text = new Dictionary<LocalizedContentManager.LanguageCode, string[]>
	{
		{ LocalizedContentManager.LanguageCode.en, new[] { "Passable Crops", "Walk through trellis crops, scarecrows, sprinklers and more. Press to choose which.", "Passable Crops", "Trellis Crops", "Scarecrows", "Sprinklers", "Forage", "Tea Bushes", "Trees - Stage", "Fruit Trees - Stage", "Weeds", "Slow down when passing", "Cross: change   Left/Right: stage   Circle: back" } },
		{ LocalizedContentManager.LanguageCode.tr, new[] { "Geçilebilir Ekinler", "Sırıklı ekinler, korkuluklar, fıskiyeler ve daha fazlasının içinden geç. Seçmek için bas.", "Geçilebilir Ekinler", "Sırıklı Ekinler", "Korkuluklar", "Fıskiyeler", "Toplanabilir Eşyalar", "Çay Çalıları", "Ağaçlar - Evre", "Meyve Ağaçları - Evre", "Yabani Otlar", "Geçerken yavaşla", "Çarpı: değiştir   Sol/Sağ: evre   Daire: geri" } },
		{ LocalizedContentManager.LanguageCode.de, new[] { "Begehbare Pflanzen", "Geh durch Rankpflanzen, Vogelscheuchen, Regner und mehr. Drücken, um sie auszuwählen.", "Begehbare Pflanzen", "Rankpflanzen", "Vogelscheuchen", "Regner", "Sammelgut", "Teesträucher", "Bäume - Stufe", "Obstbäume - Stufe", "Unkraut", "Beim Durchgehen langsamer", "Kreuz: ändern   Links/Rechts: Stufe   Kreis: zurück" } },
		{ LocalizedContentManager.LanguageCode.es, new[] { "Cultivos atravesables", "Atraviesa cultivos de enrejado, espantapájaros, aspersores y más. Pulsa para elegir cuáles.", "Cultivos atravesables", "Cultivos de enrejado", "Espantapájaros", "Aspersores", "Recolectables", "Arbustos de té", "Árboles - Fase", "Frutales - Fase", "Malas hierbas", "Ir más lento al atravesar", "Equis: cambiar   Izq./Der.: fase   Círculo: volver" } },
		{ LocalizedContentManager.LanguageCode.pt, new[] { "Plantações atravessáveis", "Atravesse plantas de treliça, espantalhos, aspersores e mais. Pressione para escolher.", "Plantações atravessáveis", "Plantas de treliça", "Espantalhos", "Aspersores", "Coletáveis", "Arbustos de chá", "Árvores - Estágio", "Frutíferas - Estágio", "Ervas daninhas", "Andar devagar ao atravessar", "X: mudar   Esq./Dir.: estágio   Círculo: voltar" } },
		{ LocalizedContentManager.LanguageCode.fr, new[] { "Cultures traversables", "Traverse les cultures sur treillis, épouvantails, arroseurs et plus. Appuie pour choisir lesquels.", "Cultures traversables", "Cultures sur treillis", "Épouvantails", "Arroseurs", "Cueillettes", "Théiers", "Arbres - Stade", "Arbres fruitiers - Stade", "Mauvaises herbes", "Ralentir en traversant", "Croix : changer   Gauche/Droite : stade   Rond : retour" } },
		{ LocalizedContentManager.LanguageCode.it, new[] { "Colture attraversabili", "Attraversa colture a traliccio, spaventapasseri, irrigatori e altro. Premi per scegliere quali.", "Colture attraversabili", "Colture a traliccio", "Spaventapasseri", "Irrigatori", "Raccolti spontanei", "Cespugli di tè", "Alberi - Fase", "Alberi da frutto - Fase", "Erbacce", "Rallenta attraversando", "Croce: cambia   Sinistra/Destra: fase   Cerchio: indietro" } },
		{ LocalizedContentManager.LanguageCode.hu, new[] { "Átjárható növények", "Sétálj át futónövényeken, madárijesztőkön, locsolókon és másokon. Nyomd meg a kiválasztáshoz.", "Átjárható növények", "Futónövények", "Madárijesztők", "Locsolók", "Gyüjthetők", "Teacserjék", "Fák - Szakasz", "Gyümölcsfák - Szakasz", "Gyomok", "Lassítás áthaladáskor", "Kereszt: váltás   Bal/Jobb: szakasz   Kör: vissza" } },
		{ LocalizedContentManager.LanguageCode.ru, new[] { "Проходимые растения", "Проходите сквозь вьющиеся растения, пугала, дождеватели и другое. Нажмите, чтобы выбрать.", "Проходимые растения", "Вьющиеся растения", "Пугала", "Дождеватели", "Дикоросы", "Чайные кусты", "Деревья - стадия", "Плодовые - стадия", "Сорняки", "Замедляться при проходе", "Крест: изменить   Влево/вправо: стадия   Круг: назад" } },
		{ LocalizedContentManager.LanguageCode.ja, new[] { "通り抜けできる作物", "つる作物、かかし、スプリンクラーなどを通り抜けられます。押して選びます。", "通り抜けできる作物", "つる作物", "かかし", "スプリンクラー", "採集物", "茶の木", "木 - 成長段階", "果樹 - 成長段階", "雑草", "通過時に減速", "決定：切替   左右：段階   キャンセル：戻る" } },
		{ LocalizedContentManager.LanguageCode.zh, new[] { "可穿过作物", "可以穿过棚架作物、稻草人、洒水器等。按下选择哪些。", "可穿过作物", "棚架作物", "稻草人", "洒水器", "采集物", "茶树", "树木 - 生长阶段", "果树 - 生长阶段", "杂草", "穿过时减速", "确认：切换   左/右：阶段   取消：返回" } },
		{ LocalizedContentManager.LanguageCode.ko, new[] { "통과 가능한 작물", "덩굴 작물, 허수아비, 스프링클러 등을 통과합니다. 눌러서 선택하세요.", "통과 가능한 작물", "덩굴 작물", "허수아비", "스프링클러", "채집물", "차나무", "나무 - 성장 단계", "과일나무 - 성장 단계", "잡초", "통과할 때 느려짐", "확인: 변경   좌/우: 단계   취소: 뒤로" } },
		{ LocalizedContentManager.LanguageCode.th, new[] { "เดินผ่านพืชได้", "เดินผ่านพืชค้าง หุ่นไล่กา สปริงเกลอร์ และอื่นๆ กดเพื่อเลือก", "เดินผ่านพืชได้", "พืชค้าง", "หุ่นไล่กา", "สปริงเกลอร์", "ของป่า", "พุ่มชา", "ต้นไม้ - ระยะเติบโต", "ไม้ผล - ระยะเติบโต", "วัชพืช", "เดินช้าลงเมื่อผ่าน", "กากบาท: เปลี่ยน   ซ้าย/ขวา: ระยะ   วงกลม: กลับ" } }
	};

	public static string Get(int index) => (Text.TryGetValue(LocalizedContentManager.CurrentLanguageCode, out string[] value) ? value : Text[LocalizedContentManager.LanguageCode.en])[index];
}

/// <summary>Extra ring slots strings. Allocated once, never per frame.</summary>
internal static class VitaRingsText
{
	public const int Label = 0, Help = 1;

	private static readonly Dictionary<LocalizedContentManager.LanguageCode, string[]> Text = new Dictionary<LocalizedContentManager.LanguageCode, string[]>
	{
		{ LocalizedContentManager.LanguageCode.en, new[] { "Extra Ring Slots", "Three more ring slots on the inventory page. Rings stay saved there while this is off and come back when it is on." } },
		{ LocalizedContentManager.LanguageCode.tr, new[] { "Ek Yüzük Yuvaları", "Envanter sayfasına üç yüzük yuvası daha ekler. Kapalıyken yüzükler orada kayıtlı kalır, açınca geri gelir." } },
		{ LocalizedContentManager.LanguageCode.de, new[] { "Zusätzliche Ringplätze", "Drei weitere Ringplätze im Inventar. Die Ringe bleiben gespeichert, solange es aus ist, und kehren beim Einschalten zurück." } },
		{ LocalizedContentManager.LanguageCode.es, new[] { "Huecos de anillo extra", "Tres huecos de anillo más en el inventario. Los anillos se guardan mientras está desactivado y vuelven al activarlo." } },
		{ LocalizedContentManager.LanguageCode.pt, new[] { "Espaços extras de anel", "Mais três espaços de anel no inventário. Os anéis ficam salvos enquanto está desligado e voltam ao ligar." } },
		{ LocalizedContentManager.LanguageCode.fr, new[] { "Emplacements d'anneau en plus", "Trois emplacements d'anneau de plus dans l'inventaire. Les anneaux restent sauvegardés quand c'est désactivé et reviennent à l'activation." } },
		{ LocalizedContentManager.LanguageCode.it, new[] { "Slot anello extra", "Tre slot anello in più nell'inventario. Gli anelli restano salvati quando è disattivato e tornano quando lo riattivi." } },
		{ LocalizedContentManager.LanguageCode.hu, new[] { "Extra gyürühelyek", "Három további gyürühely a leltárban. Kikapcsolva a gyürük mentve maradnak, bekapcsoláskor visszajönnek." } },
		{ LocalizedContentManager.LanguageCode.ru, new[] { "Доп. слоты колец", "Ещё три слота для колец в инвентаре. Пока выключено, кольца хранятся в сохранении и вернутся при включении." } },
		{ LocalizedContentManager.LanguageCode.ja, new[] { "追加の指輪枠", "持ち物画面に指輪枠を3つ追加します。オフの間も指輪は保存され、オンにすると戻ります。" } },
		{ LocalizedContentManager.LanguageCode.zh, new[] { "额外戒指栏", "在物品栏中增加三个戒指栏。关闭时戒指仍保存在存档中，开启后恢复。" } },
		{ LocalizedContentManager.LanguageCode.ko, new[] { "추가 반지 칸", "인벤토리에 반지 칸 3개를 추가합니다. 꺼져 있어도 반지는 저장되며 다시 켜면 돌아옵니다." } },
		{ LocalizedContentManager.LanguageCode.th, new[] { "ช่องแหวนเพิ่ม", "เพิ่มช่องแหวนอีกสามช่องในหน้ากระเป๋า เมื่อปิด แหวนยังถูกบันทึกไว้และกลับมาเมื่อเปิด" } },
	};

	public static string Get(int index) => (Text.TryGetValue(LocalizedContentManager.CurrentLanguageCode, out string[] value) ? value : Text[LocalizedContentManager.LanguageCode.en])[index];
}
