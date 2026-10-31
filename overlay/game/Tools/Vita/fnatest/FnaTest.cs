// FNA-on-Vita smoke test: a real FNA Game on the Vita Mono runtime, SDL3, FNA3D and FAudio.
// Draws 1024 sprites per frame with SpriteBatch (as Stardew draws nearly everything), checks
// Texture2D.SetData/GetData, reads the gamepad, plays sound through FAudio (a looping tone whose
// pitch sweeps, plus a chime on Cross), and logs FNA's own messages and fps to fna.txt.
// Quits after 20 seconds or on Start.
using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

class FnaTest : Game
{
	const int Sprites = 1024;

	readonly GraphicsDeviceManager graphics;
	readonly StreamWriter log;
	SpriteBatch batch;
	Texture2D texture;
	SoundEffect tone;
	SoundEffect chime;
	SoundEffectInstance toneLoop;
	bool crossWasDown;
	int chimes;
	double elapsed;
	double lastReport;
	int framesSinceReport;
	int totalFrames;

	FnaTest(StreamWriter log)
	{
		this.log = log;
		graphics = new GraphicsDeviceManager(this);
		graphics.PreferredBackBufferWidth = 960;
		graphics.PreferredBackBufferHeight = 544;
		graphics.IsFullScreen = true;
		graphics.SynchronizeWithVerticalRetrace = true;
		IsFixedTimeStep = false;
	}

	void Log(string format, params object[] args)
	{
		log.WriteLine("[{0,7:F2}s] {1}", elapsed, string.Format(format, args));
	}

	protected override void LoadContent()
	{
		Log("LoadContent: adapter {0}, back buffer {1}x{2}, profile {3}",
			GraphicsAdapter.DefaultAdapter.Description,
			GraphicsDevice.PresentationParameters.BackBufferWidth,
			GraphicsDevice.PresentationParameters.BackBufferHeight,
			GraphicsDevice.GraphicsProfile);
		object titleLocation = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.FNAPlatform")
			.GetField("TitleLocation").GetValue(null);
		Log("TitleLocation {0}", titleLocation);
		// Content loads through TitleContainer, so this is the path every asset will take.
		using (var reader = new StreamReader(TitleContainer.OpenStream("Content/test.txt")))
			Log("TitleContainer read Content/test.txt: \"{0}\"", reader.ReadToEnd().Trim());

		batch = new SpriteBatch(GraphicsDevice);
		texture = new Texture2D(GraphicsDevice, 64, 64);
		var pixels = new Color[64 * 64];
		for (int y = 0; y < 64; y++)
			for (int x = 0; x < 64; x++)
				pixels[y * 64 + x] = ((x / 8 + y / 8) & 1) != 0 ? Color.White : new Color(x * 4, y * 4, 128);
		texture.SetData(pixels);

		var readBack = new Color[64 * 64];
		texture.GetData(readBack);
		int differences = 0;
		for (int i = 0; i < pixels.Length; i++)
			if (readBack[i] != pixels[i])
				differences++;
		Log("Texture2D GetData: {0} of {1} pixels differ{2}", differences, pixels.Length, differences == 0 ? " (exact)" : "");

		// Audio: PCM built in memory, so this checks FAudio and SDL's Vita audio without content.
		try
		{
			tone = new SoundEffect(MakeTone(220, 1.0f, 0.25f, false), 44100, AudioChannels.Mono);
			chime = new SoundEffect(MakeTone(880, 0.4f, 0.5f, true), 44100, AudioChannels.Mono);
			toneLoop = tone.CreateInstance();
			toneLoop.IsLooped = true;
			toneLoop.Volume = 0.5f;
			toneLoop.Play();
			Log("audio: tone {0:F2} s, chime {1:F2} s, loop state {2}, master volume {3}",
				tone.Duration.TotalSeconds, chime.Duration.TotalSeconds, toneLoop.State, SoundEffect.MasterVolume);
		}
		catch (Exception e)
		{
			Log("audio failed: {0}", e);
		}
	}

	// 16-bit mono sine; a whole number of cycles per buffer so the loop has no click.
	static byte[] MakeTone(int hertz, float seconds, float amplitude, bool decay)
	{
		int samples = (int)(44100 * seconds);
		samples -= samples % (44100 / hertz);
		var pcm = new byte[samples * 2];
		for (int i = 0; i < samples; i++)
		{
			double envelope = decay ? 1.0 - (double)i / samples : 1.0;
			short value = (short)(short.MaxValue * amplitude * envelope * Math.Sin(2 * Math.PI * hertz * i / 44100.0));
			pcm[i * 2] = (byte)value;
			pcm[i * 2 + 1] = (byte)(value >> 8);
		}
		return pcm;
	}

	protected override void Update(GameTime gameTime)
	{
		elapsed += gameTime.ElapsedGameTime.TotalSeconds;
		GamePadState pad = GamePad.GetState(PlayerIndex.One);
		if (toneLoop != null)
			toneLoop.Pitch = (float)(0.5 * Math.Sin(elapsed * 0.8));
		bool crossDown = pad.Buttons.A == ButtonState.Pressed;
		if (crossDown && !crossWasDown && chime != null)
		{
			chime.Play(1.0f, (chimes % 5) * 0.2f - 0.4f, 0.0f);
			chimes++;
			Log("chime {0}", chimes);
		}
		crossWasDown = crossDown;
		if (pad.Buttons.Start == ButtonState.Pressed || elapsed > 20)
		{
			Log("exit requested ({0}), tone loop {1}", pad.Buttons.Start == ButtonState.Pressed ? "Start" : "20 s",
				toneLoop != null ? toneLoop.State.ToString() : "none");
			Exit();
		}
		base.Update(gameTime);
	}

	protected override void Draw(GameTime gameTime)
	{
		GraphicsDevice.Clear(Color.CornflowerBlue);
		float t = (float)elapsed;
		batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
		for (int i = 0; i < Sprites; i++)
		{
			int gx = i % 32, gy = i / 32;
			int x = (int)(20 + gx * 29 + 6 * Math.Sin(t * 2 + i * 0.37));
			int y = (int)(8 + gy * 16.5 + 4 * Math.Cos(t * 1.5 + i * 0.21));
			batch.Draw(texture, new Rectangle(x, y, 24, 24), new Color(128 + gx * 4, 128 + gy * 4, 255));
		}
		batch.End();
		base.Draw(gameTime);

		totalFrames++;
		framesSinceReport++;
		if (totalFrames == 1)
			Log("first frame drawn");
		if (elapsed - lastReport >= 5)
		{
			Log("{0:F1} fps, managed heap {1} KB, gen0 collections {2}",
				framesSinceReport / (elapsed - lastReport), GC.GetTotalMemory(false) / 1024, GC.CollectionCount(0));
			lastReport = elapsed;
			framesSinceReport = 0;
		}
	}

	static int Main(string[] args)
	{
		string dir = args.Length > 0 ? args[0] : "/ux0:/data/fnatest";
		var log = new StreamWriter(Path.Combine(dir, "fna.txt"));
		log.AutoFlush = true;
		FNALoggerEXT.LogInfo = message => log.WriteLine("FNA info: " + message);
		FNALoggerEXT.LogWarn = message => log.WriteLine("FNA warn: " + message);
		FNALoggerEXT.LogError = message => log.WriteLine("FNA ERROR: " + message);
		try
		{
			using (var game = new FnaTest(log))
			{
				game.Run();
				log.WriteLine("Run returned after {0} frames", game.totalFrames);
			}
		}
		catch (Exception e)
		{
			log.WriteLine("Unhandled: " + e);
			log.Close();
			return 1;
		}
		log.Close();
		return 42;
	}
}
