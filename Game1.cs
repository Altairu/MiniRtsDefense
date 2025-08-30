using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Media;
using System;
using System.Collections.Generic;

namespace MiniRtsDefense
{
    public class Game1 : Game
    {
        GraphicsDeviceManager _g;
        SpriteBatch _sb;
        SpriteFont _font; // 後で LoadContent で読み込む

        const int GRID_W = 20, GRID_H = 12;
        const int BASE_TILE = 40;
        const float FPS = 60f;
        const float DT = 1f / FPS;
        const float HQ_HP_MAX = 200f;

        readonly int SIDEBAR_TILES = 6;

        enum GameState { Menu, HowTo, Playing, Paused, GameOver }
        GameState _state = GameState.Menu;

        float _scale = 1f;
        int _ox = 0, _oy = 0, _sidebarPx = 0;

        MouseState _ms, _pms;
        KeyboardState _ks, _pks;

        Texture2D _pix;

        char[,] _tiles = new char[GRID_W, GRID_H];
        Cell?[,] _struct = new Cell?[GRID_W, GRID_H];
        List<Cell> _hqStack = new();

        int[,] _flow = new int[GRID_W, GRID_H];

        float _gold = 60f;
        int _wave = 1;
        float _waveTimer = 12f;
        float _spawnCd = 0f;
        int _spawned = 0;
        float _hqHp = HQ_HP_MAX;

        List<Enemy> _enemies = new();
        List<Bullet> _bullets = new();
        List<Bullet> _enemyBullets = new();

        BuildingType _sel = BuildingType.Turret;

        Dictionary<BuildingType, int> _limits = new()
        {
            { BuildingType.Turret, 15 },
            { BuildingType.Wall,   30 },
            { BuildingType.Tesla,  10 },
        };
        Dictionary<BuildingType, int> _counts = new()
        {
            { BuildingType.Wall,   0 },
            { BuildingType.Turret, 0 },
            { BuildingType.Tesla,  0 },
        };

        Point _hq;
        Random _rand = new(42);

        Song _bgm; // BGM
        float _musicVol = 0.5f; // 0=OFF, 0.5=半分, 1=ON
        GameState _lastMusicState = (GameState)(-1);
        // BGM ボタン用レイアウト計算補助
        Rectangle _btnVolOn, _btnVolHalf, _btnVolOff;

        public Game1()
        {
            _g = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            Window.AllowUserResizing = true;
            _g.PreferredBackBufferWidth = 1280;
            _g.PreferredBackBufferHeight = 720;
        }

        protected override void Initialize()
        {
            _pix = new Texture2D(GraphicsDevice, 1, 1);
            _pix.SetData(new[] { Color.White });

            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    _tiles[x, y] = '.';

            _hq = new Point(GRID_W / 2, GRID_H / 2);
            _tiles[_hq.X, _hq.Y] = 'H';
            GenerateOres(28);

            RecomputeFlow();
            base.Initialize();
        }

        protected override void LoadContent()
        {
            _sb = new SpriteBatch(GraphicsDevice);
            _font = Content.Load<SpriteFont>("DefaultFont");
            // bgm.mp3 (または orchestral_mission.mp3) を Content.mgcb に追加しておく必要あり
            // 優先: "bgm" -> 失敗したら "orchestral_mission" を試す
            Song TryLoad(string name)
            {
                try { return Content.Load<Song>(name); } catch { return null; }
            }
            _bgm = TryLoad("bgm") ?? TryLoad("orchestral_mission");
            if (_bgm != null)
            {
                MediaPlayer.IsRepeating = true;
                MediaPlayer.Volume = _musicVol;
                try { MediaPlayer.Play(_bgm); } catch { }
                _lastMusicState = _state;
            }
        }

        protected override void Update(GameTime gameTime)
        {
            _pms = _ms; _ms = Mouse.GetState();
            _pks = _ks; _ks = Keyboard.GetState();

            if (Pressed(Keys.F11)) _g.HardwareModeSwitch = !_g.HardwareModeSwitch;
            if (Pressed(Keys.Escape))
            {
                if (_state == GameState.Playing || _state == GameState.GameOver)
                {
                    _state = GameState.Menu;
                    base.Update(gameTime);
                    return;
                }
            }

            UpdateScaleAndOffsets();

            if (_state == GameState.Menu)
            {
                // メニュー内クリック判定
                if (ClickedLeft())
                {
                    MenuLayout(out var playBtn, out var howBtn, _scale);
                    MusicLayout(out var volOn, out var volHalf, out var volOff, _scale);
                    if (MouseIn(playBtn)) { StartGame(); base.Update(gameTime); return; }
                    if (MouseIn(howBtn)) { _state = GameState.HowTo; base.Update(gameTime); return; }
                    if (MouseIn(volOn)) { SetMusicVolume(1f); }
                    else if (MouseIn(volHalf)) { SetMusicVolume(0.5f); }
                    else if (MouseIn(volOff)) { SetMusicVolume(0f); }
                }
                base.Update(gameTime); return;
            }

            if (_state == GameState.HowTo)
            {
                if (ClickedLeft())
                {
                    HowToLayout(out var backBtn, out var playBtn, _scale);
                    if (MouseIn(playBtn)) { StartGame(); base.Update(gameTime); return; }
                    if (MouseIn(backBtn)) { _state = GameState.Menu; base.Update(gameTime); return; }
                }
                base.Update(gameTime); return;
            }

            if (Pressed(Keys.P))
                _state = _state == GameState.Playing ? GameState.Paused : GameState.Playing;

            if (_state == GameState.Paused || _state == GameState.GameOver)
            {
                base.Update(gameTime);
                return;
            }

            if (Pressed(Keys.D1)) _sel = BuildingType.Wall;
            if (Pressed(Keys.D2)) _sel = BuildingType.Turret;
            if (Pressed(Keys.D3)) _sel = BuildingType.Miner;
            if (Pressed(Keys.D4)) _sel = BuildingType.Tesla;
            if (Pressed(Keys.D5)) _sel = BuildingType.Healer;

            if (ClickedLeft()) PlaceOrUpgrade();
            if (ClickedRight()) RemoveStructure();

            _waveTimer -= DT;
            if (_waveTimer <= 0)
            {
                var p = WaveParams(_wave);
                if (_spawned < p.Count)
                {
                    _spawnCd -= DT;
                    if (_spawnCd <= 0)
                    {
                        SpawnEnemy(p);
                        _spawned++;
                        _spawnCd = MathF.Max(0.25f, p.Rate);
                    }
                }
                else if (_enemies.Count == 0)
                {
                    _wave++;
                    _waveTimer = 10f;
                    _spawned = 0;
                }
            }

            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    if (_struct[x, y] is Cell s) UpdateBuilding(x, y, s);

            foreach (var s in _hqStack.ToArray())
                UpdateBuilding(_hq.X, _hq.Y, s);

            foreach (var b in _bullets.ToArray())
            {
                b.Pos += b.Vel * (DT * BASE_TILE);
                b.Life -= DT;
                if (b.Life <= 0) { _bullets.Remove(b); continue; }

                foreach (var e in _enemies.ToArray())
                {
                    if (Vector2.Distance(b.Pos, e.Pos) < 14)
                    {
                        e.Hp -= b.Dmg;
                        if (b.SlowFactor.HasValue) { e.SlowTimer = 0.8f; e.SlowFactor = b.SlowFactor.Value; }
                        _bullets.Remove(b);
                        if (e.Hp <= 0) { _gold += 3; _enemies.Remove(e); }
                        break;
                    }
                }
            }

            foreach (var eb in _enemyBullets.ToArray())
            {
                eb.Pos += eb.Vel * (DT * BASE_TILE);
                eb.Life -= DT;
                if (eb.Life <= 0) { _enemyBullets.Remove(eb); continue; }

                if (_hqStack.Count > 0 && Vector2.Distance(eb.Pos, GridCenter(_hq)) < 14)
                {
                    var target = _hqStack[^1];
                    target.Hp -= eb.Dmg;
                    if (target.Hp <= 0)
                    {
                        if (target.Blocks && _counts.ContainsKey(target.Type))
                            _counts[target.Type] = Math.Max(0, _counts[target.Type] - 1);
                        _hqStack.RemoveAt(_hqStack.Count - 1);
                        RecomputeFlow();
                    }
                    _enemyBullets.Remove(eb);
                    continue;
                }

                var gp = GridAtScreen(eb.Pos);
                if (InBounds(gp) && _struct[gp.X, gp.Y] is Cell sc &&
                    Vector2.Distance(eb.Pos, GridCenter(gp)) < 14)
                {
                    sc.Hp -= eb.Dmg;
                    if (sc.Hp <= 0)
                    {
                        if (sc.Blocks && _counts.ContainsKey(sc.Type))
                            _counts[sc.Type] = Math.Max(0, _counts[sc.Type] - 1);
                        _struct[gp.X, gp.Y] = null;
                        if (sc.Blocks) RecomputeFlow();
                    }
                    _enemyBullets.Remove(eb);
                    continue;
                }

                if (Vector2.Distance(eb.Pos, GridCenter(_hq)) < 16)
                {
                    _hqHp -= eb.Dmg;
                    _enemyBullets.Remove(eb);
                    if (_hqHp <= 0) _state = GameState.GameOver;
                    continue;
                }
            }

            foreach (var e in _enemies.ToArray())
            {
                var gp = GridAtScreen(e.Pos);
                float spdScale = (e.SlowTimer > 0) ? e.SlowFactor : 1f;
                e.SlowTimer = MathF.Max(0, e.SlowTimer - DT);
                if (e.SlowTimer <= 0) e.SlowFactor = 1f;

                if (gp == _hq || Vector2.Distance(e.Pos, GridCenter(_hq)) < 16)
                {
                    _hqHp -= e.Dps * DT;
                    if (_hqHp <= 0) _state = GameState.GameOver;
                    continue;
                }

                if (TryFindBlocking(e.Pos, 18, out int bx, out int by, out Cell? bs, out bool isHq))
                {
                    if (bs is Cell bsc)
                    {
                        bsc.Hp -= e.Dps * DT;
                        if (bsc.Hp <= 0)
                        {
                            if (bsc.Blocks && _counts.ContainsKey(bsc.Type))
                                _counts[bsc.Type] = Math.Max(0, _counts[bsc.Type] - 1);
                            if (isHq) _hqStack.Remove(bsc);
                            else _struct[bx, by] = null;
                            RecomputeFlow();
                        }
                    }
                    continue;
                }

                if (_flow[gp.X, gp.Y] >= 9999)
                {
                    var tc = FindClosestBlockCenter(e.Pos);
                    if (tc.HasValue)
                        e.Pos += Vector2.Normalize(tc.Value - e.Pos) * e.Spd * spdScale;
                }
                else
                {
                    var next = NextStep(gp);
                    var tc = GridCenter(next);
                    e.Pos += Vector2.Normalize(tc - e.Pos) * e.Spd * spdScale;
                }

                if (e.Type == EnemyType.Shooter)
                {
                    e.ShootCd = MathF.Max(0, e.ShootCd - DT);
                    float rng = e.ShootRange * BASE_TILE;
                    Vector2? target = null; float td = 1e9f;

                    for (int x = 0; x < GRID_W; x++)
                        for (int y = 0; y < GRID_H; y++)
                            if (_struct[x, y] is Cell s)
                            {
                                var c = GridCenter(new Point(x, y));
                                float d = Vector2.Distance(e.Pos, c);
                                if (d < rng && d < td) { td = d; target = c; }
                            }

                    if (_hqStack.Count > 0)
                    {
                        var c = GridCenter(_hq);
                        float d = Vector2.Distance(e.Pos, c);
                        if (d < rng && d < td) { td = d; target = c; }
                    }

                    {
                        var c = GridCenter(_hq);
                        float d = Vector2.Distance(e.Pos, c);
                        if (d < rng && d < td) target = c;
                    }

                    if (target.HasValue && e.ShootCd <= 0)
                    {
                        var dir = Vector2.Normalize(target.Value - e.Pos);
                        _enemyBullets.Add(new Bullet { Pos = e.Pos, Vel = dir * 6f, Dmg = (int)e.Dps, Life = 2f });
                        e.ShootCd = e.ShootCdMax;
                    }
                }
            }

            base.Update(gameTime);
            UpdateMusic();
        }

        void UpdateMusic()
        {
            if (_bgm == null) return;
            // ユーザー指定音量をそのまま反映
            if (Math.Abs(MediaPlayer.Volume - _musicVol) > 0.001f)
                MediaPlayer.Volume = _musicVol;
            if (_musicVol <= 0f && MediaPlayer.State == MediaState.Playing)
                MediaPlayer.Pause();
            else if (_musicVol > 0f && MediaPlayer.State == MediaState.Paused)
                MediaPlayer.Resume();
        }

        void SetMusicVolume(float v)
        {
            _musicVol = MathHelper.Clamp(v, 0f, 1f);
            // 直ちに反映
            MediaPlayer.Volume = _musicVol;
            if (_musicVol <= 0f)
            {
                if (MediaPlayer.State == MediaState.Playing)
                    MediaPlayer.Pause();
            }
            else
            {
                if (MediaPlayer.State != MediaState.Playing)
                {
                    try { MediaPlayer.Resume(); } catch { /* 失敗時は無視 */ }
                }
            }
        }

        protected override void Draw(GameTime gameTime)
        {
            GraphicsDevice.Clear(new Color(11, 14, 20));

            _sb.Begin(samplerState: SamplerState.PointClamp);

            float s = _scale;
            int TILE = (int)(BASE_TILE * s);
            int gridWpx = GRID_W * TILE;
            int totalW = gridWpx + _sidebarPx;
            int totalH = GRID_H * TILE;

            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                {
                    int X = _ox + x * TILE;
                    int Y = _oy + y * TILE;
                    char t = _tiles[x, y];
                    var col = new Color(15, 23, 34);
                    if (t == 'O') col = new Color(30, 58, 30);
                    if (x == _hq.X && y == _hq.Y) col = new Color(31, 42, 68);
                    FillRect(X, Y, TILE, TILE, col);
                }

            FillRect(_ox + _hq.X * TILE + (int)(4 * s), _oy + _hq.Y * TILE + (int)(4 * s),
                     TILE - (int)(8 * s), TILE - (int)(8 * s), new Color(59, 130, 246));

            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    if (_struct[x, y] is Cell sc) DrawBuilding(x, y, sc, TILE, s, 0);

            for (int i = 0; i < _hqStack.Count; i++)
                DrawBuilding(_hq.X, _hq.Y, _hqStack[i], TILE, s, -(int)(2 * s) * i);

            foreach (var b in _bullets)
            {
                FillCircle(Screen(b.Pos), (int)(3 * s), new Color(253, 230, 138));
                if (b.SlowFactor.HasValue)
                    DrawCircle(Screen(b.Pos), (int)(6 * s), new Color(224, 242, 254));
            }

            foreach (var eb in _enemyBullets)
                FillCircle(Screen(eb.Pos), (int)(3 * s), new Color(252, 165, 165));

            foreach (var e in _enemies)
            {
                Color col; int r;
                switch (e.Type)
                {
                    case EnemyType.Tank: col = new Color(124, 58, 237); r = (int)(12 * s); break;
                    case EnemyType.Scout: col = new Color(249, 115, 22); r = (int)(8 * s); break;
                    case EnemyType.Shooter: col = new Color(239, 68, 68); r = (int)(10 * s); break;
                    case EnemyType.Boss: col = new Color(14, 165, 164); r = (int)(18 * s); break;
                    default: col = new Color(239, 68, 68); r = (int)(10 * s); break;
                }
                FillCircle(Screen(e.Pos), r, col);

                float hpRatio = e.Hp / MathF.Max(1, e.MaxHp);
                FillRect(Screen(e.Pos).X - (int)(12 * s),
                         Screen(e.Pos).Y + (int)(12 * s),
                         (int)(24 * s * hpRatio), (int)(3 * s),
                         new Color(34, 197, 94));
            }

            int sx0 = _ox + gridWpx;
            FillRect(sx0, _oy, _sidebarPx, totalH, new Color(17, 24, 39));

            DrawText(sx0 + (int)(16 * s), _oy + (int)(18 * s), $"ウェーブ {_wave}", Color.White, (int)(18 * s));
            DrawText(sx0 + (int)(16 * s), _oy + (int)(46 * s), $"ゴールド {(int)_gold}", Color.White, (int)(14 * s));

            int hpw = (int)(200 * s * (_hqHp / HQ_HP_MAX));
            FillRect(sx0 + (int)(16 * s), _oy + (int)(76 * s), hpw, (int)(14 * s), new Color(52, 211, 153));
            DrawRect(sx0 + (int)(16 * s), _oy + (int)(76 * s), (int)(216 * s), (int)(14 * s), new Color(31, 41, 55));
            // 拠点HP数値（中央表示)
            int hpCenterX = sx0 + (int)(16 * s) + (int)(216 * s) / 2;
            int hpCenterY = _oy + (int)(76 * s) + (int)(7 * s);
            DrawText(hpCenterX, hpCenterY, $"拠点HP {(int)_hqHp}/{(int)HQ_HP_MAX}", Color.White, (int)(12 * s), true);

            int y0 = _oy + (int)(110 * s);
            DrawBuildButton(sx0 + (int)(16 * s), y0 + (int)(0 * s), BuildingType.Wall, "1. 壁 (5G)", s);
            DrawBuildButton(sx0 + (int)(16 * s), y0 + (int)(70 * s), BuildingType.Turret, "2. 砲台 (10G)", s);
            DrawBuildButton(sx0 + (int)(16 * s), y0 + (int)(140 * s), BuildingType.Miner, "3. 採掘機 (20G)", s);
            DrawBuildButton(sx0 + (int)(16 * s), y0 + (int)(210 * s), BuildingType.Tesla, "4. テスラ (30G)", s);
            DrawBuildButton(sx0 + (int)(16 * s), y0 + (int)(280 * s), BuildingType.Healer, "5. 回復塔 (25G)", s);

            var mg = MouseGrid();
            if (InBounds(mg))
            {
                int X = _ox + mg.X * TILE;
                int Y = _oy + mg.Y * TILE;
                bool ok = CanPlacePreview(mg);
                DrawRect(X, Y, TILE, TILE, ok ? new Color(34, 197, 94) : new Color(239, 68, 68), Math.Max(1, (int)(2 * s)));
            }

            if (_state == GameState.Menu)
            {
                DrawMainMenu(totalW, totalH, s);
            }
            else if (_state == GameState.HowTo)
            {
                DrawHowTo(totalW, totalH, s);
            }
            else if (_state == GameState.GameOver)
            {
                int cx = _ox + totalW / 2;
                int cy = _oy + totalH / 2;
                DrawText(cx, cy, "GAME OVER", Color.OrangeRed, (int)(42 * s), true);
                DrawText(cx, cy + (int)(40 * s), "Escでメニューへ", Color.White, (int)(18 * s), true);
            }

            _sb.End();
            base.Draw(gameTime);
        }

        // ====== 補助構造 ======
        enum BuildingType { Wall, Turret, Miner, Tesla, Healer }
        class Cell
        {
            public BuildingType Type;
            public float Hp, MaxHp;
            public bool Blocks;
            public int Level = 1;
            public int NextCost;
            public int Spent;
            public float Cool;

            public float FireInterval = 1f;
            public float Range = 5f;
            public int Dmg = 12;

            public float MineRate = 4f;

            public float TeslaSlow = 0.60f;

            public float HealRange = 3f;
            public float HealRate = 6f;
            public bool SelfHeal = true;
        }

        enum EnemyType { Grunt, Tank, Scout, Shooter, Boss }
        class Enemy
        {
            public Vector2 Pos;
            public float Hp, MaxHp, Spd, Dps;
            public EnemyType Type;
            public float SlowTimer = 0f, SlowFactor = 1f;
            public float ShootRange = 0f, ShootCd = 0f, ShootCdMax = 1.6f;
        }
        class Bullet
        {
            public Vector2 Pos;
            public Vector2 Vel;
            public int Dmg;
            public float Life;
            public float? SlowFactor;
        }

        // ====== ロジック ======
        void StartGame()
        {
            _state = GameState.Playing;
            _struct = new Cell?[GRID_W, GRID_H];
            _hqStack.Clear();
            _enemies.Clear();
            _bullets.Clear();
            _enemyBullets.Clear();
            _gold = 60f;
            _wave = 1;
            _waveTimer = 8f;
            _spawnCd = 0f;
            _spawned = 0;
            _hqHp = HQ_HP_MAX;
            _counts[BuildingType.Wall] = 0;
            _counts[BuildingType.Turret] = 0;
            _counts[BuildingType.Tesla] = 0;
            RecomputeFlow();
        }

        void GenerateOres(int n)
        {
            for (int i = 0; i < n; i++)
            {
                int x = _rand.Next(GRID_W);
                int y = _rand.Next(GRID_H);
                if (x == _hq.X && y == _hq.Y) { i--; continue; }
                _tiles[x, y] = 'O';
            }
        }

        struct WaveP { public float Hp, Spd, Dps; public int Count; public float Rate; }
        WaveP WaveParams(int w) => new()
        {
            Hp = 24 + 6 * w,
            Spd = 1.6f + 0.06f * w,
            Dps = 10 + 2 * w,
            Count = 8 + 2 * w,
            Rate = MathF.Max(0.25f, 0.8f - 0.02f * w)
        };

        void SpawnEnemy(WaveP p)
        {
            string side = new[] { "top", "bottom", "left", "right" }[_rand.Next(4)];
            Point sp = side switch
            {
                "top" => new Point(_rand.Next(GRID_W), 0),
                "bottom" => new Point(_rand.Next(GRID_W), GRID_H - 1),
                "left" => new Point(0, _rand.Next(GRID_H)),
                _ => new Point(GRID_W - 1, _rand.Next(GRID_H))
            };

            EnemyType type;
            if (_wave % 3 == 0 && _spawned == 0) type = EnemyType.Boss;
            else
            {
                float r = (float)_rand.NextDouble();
                type = r < 0.55f ? EnemyType.Grunt
                     : r < 0.72f ? EnemyType.Scout
                     : r < 0.90f ? EnemyType.Tank
                     : EnemyType.Shooter;
            }

            float ws = 1f + 0.12f * MathF.Max(0, _wave - 1);
            float hp = p.Hp * HpMul(type) * ws;
            float spd = p.Spd * SpdMul(type) * (1f + 0.03f * MathF.Max(0, _wave - 1));
            float dps = p.Dps * DpsMul(type) * ws;

            var e = new Enemy
            {
                Pos = GridCenter(sp),
                Hp = hp,
                MaxHp = hp,
                Spd = spd,
                Dps = dps,
                Type = type,
                ShootRange = type == EnemyType.Shooter ? 6 : 0,
                ShootCdMax = 1.6f,
                ShootCd = 0
            };
            _enemies.Add(e);
        }

        static float HpMul(EnemyType t) => t switch
        { EnemyType.Tank => 2.8f, EnemyType.Scout => 0.6f, EnemyType.Shooter => 0.9f, EnemyType.Boss => 6f, _ => 1f };
        static float SpdMul(EnemyType t) => t switch
        { EnemyType.Tank => 0.55f, EnemyType.Scout => 1.7f, EnemyType.Shooter => 0.9f, EnemyType.Boss => 0.75f, _ => 1f };
        static float DpsMul(EnemyType t) => t switch
        { EnemyType.Tank => 1.2f, EnemyType.Scout => 0.6f, EnemyType.Shooter => 0.8f, EnemyType.Boss => 2.5f, _ => 1f };

        void UpdateBuilding(int gx, int gy, Cell s)
        {
            s.Cool = MathF.Max(0, s.Cool - DT);
            switch (s.Type)
            {
                case BuildingType.Miner:
                    if (_tiles[gx, gy] == 'O') _gold += s.MineRate * DT;
                    break;

                case BuildingType.Turret:
                    if (s.Cool <= 0)
                    {
                        var target = FindTarget(gx, gy, s.Range);
                        if (target != null)
                        {
                            FireBullet(GridCenter(new Point(gx, gy)), target.Pos, s.Dmg, 8f, 1.2f, null);
                            s.Cool = s.FireInterval;
                        }
                    }
                    break;

                case BuildingType.Tesla:
                    if (s.Cool <= 0)
                    {
                        var targets = FindTargets(gx, gy, s.Range, 3);
                        Vector2 p = GridCenter(new Point(gx, gy));
                        foreach (var e in targets)
                        {
                            FireBullet(p, e.Pos, s.Dmg, 12f, 0.3f, s.TeslaSlow);
                            p = e.Pos;
                        }
                        s.Cool = s.FireInterval;
                    }
                    break;

                case BuildingType.Healer:
                    float rng = s.HealRange;
                    float rate = s.HealRate * DT;

                    for (int x = 0; x < GRID_W; x++)
                        for (int y = 0; y < GRID_H; y++)
                            if (_struct[x, y] is Cell t2)
                                if (!(x == gx && y == gy && !s.SelfHeal))
                                    if (Vector2.Distance(GridCenter(new Point(gx, gy)), GridCenter(new Point(x, y))) <= rng * BASE_TILE)
                                        t2.Hp = MathF.Min(t2.MaxHp, t2.Hp + rate);

                    if (_hqStack.Count > 0 &&
                        Vector2.Distance(GridCenter(new Point(gx, gy)), GridCenter(_hq)) <= rng * BASE_TILE)
                        foreach (var t2 in _hqStack)
                            t2.Hp = MathF.Min(t2.MaxHp, t2.Hp + rate);

                    if (Vector2.Distance(GridCenter(new Point(gx, gy)), GridCenter(_hq)) <= rng * BASE_TILE)
                        _hqHp = MathF.Min(HQ_HP_MAX, _hqHp + rate * 0.10f);
                    break;
            }
        }

        Enemy? FindTarget(int gx, int gy, float rng)
        {
            var c = GridCenter(new Point(gx, gy));
            Enemy? best = null; float bd = float.MaxValue;
            foreach (var e in _enemies)
            {
                float d = Vector2.Distance(c, e.Pos);
                if (d <= rng * BASE_TILE && d < bd) { bd = d; best = e; }
            }
            return best;
        }

        List<Enemy> FindTargets(int gx, int gy, float rng, int k)
        {
            var c = GridCenter(new Point(gx, gy));
            var cand = new List<(float d, Enemy e)>();
            foreach (var e in _enemies)
            {
                float d = Vector2.Distance(c, e.Pos);
                if (d <= rng * BASE_TILE) cand.Add((d, e));
            }
            cand.Sort((a, b) => a.d.CompareTo(b.d));
            var res = new List<Enemy>();
            for (int i = 0; i < Math.Min(k, cand.Count); i++) res.Add(cand[i].e);
            return res;
        }

        void FireBullet(Vector2 s, Vector2 t, int dmg, float speed, float life, float? slow)
        {
            var dir = Vector2.Normalize(t - s);
            _bullets.Add(new Bullet { Pos = s, Vel = dir * speed, Dmg = dmg, Life = life, SlowFactor = slow });
        }

        void RecomputeFlow()
        {
            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    _flow[x, y] = 9999;

            Queue<Point> q = new();
            _flow[_hq.X, _hq.Y] = 0;
            q.Enqueue(_hq);
            var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };

            while (q.Count > 0)
            {
                var p = q.Dequeue();
                int d0 = _flow[p.X, p.Y];
                foreach (var d in dirs)
                {
                    var n = new Point(p.X + d.X, p.Y + d.Y);
                    if (!InBounds(n)) continue;

                    bool blocked = false;
                    if (_struct[n.X, n.Y] is Cell s && s.Blocks) blocked = true;
                    if (n == _hq && _hqStack.Count > 0) blocked = true;
                    if (blocked) continue;

                    if (_flow[n.X, n.Y] > d0 + 1)
                    {
                        _flow[n.X, n.Y] = d0 + 1;
                        q.Enqueue(n);
                    }
                }
            }
        }

        Point NextStep(Point gp)
        {
            var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };
            Point best = gp; int bd = _flow[gp.X, gp.Y];
            foreach (var d in dirs)
            {
                var n = new Point(gp.X + d.X, gp.Y + d.Y);
                if (!InBounds(n)) continue;
                if (_flow[n.X, n.Y] < bd) { bd = _flow[n.X, n.Y]; best = n; }
            }
            return best;
        }

        bool TryFindBlocking(Vector2 pos, int radius, out int bx, out int by, out Cell? bs, out bool isHq)
        {
            if (_hqStack.Count > 0 && Vector2.Distance(pos, GridCenter(_hq)) <= radius)
            { bx = _hq.X; by = _hq.Y; bs = _hqStack[^1]; isHq = true; return true; }

            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    if (_struct[x, y] is Cell s && s.Blocks)
                        if (Vector2.Distance(pos, GridCenter(new Point(x, y))) <= radius)
                        { bx = x; by = y; bs = s; isHq = false; return true; }

            bx = by = 0; bs = null; isHq = false; return false;
        }

        Vector2? FindClosestBlockCenter(Vector2 pos)
        {
            float best = float.MaxValue; Vector2? bc = null;
            if (_hqStack.Count > 0)
            {
                var c = GridCenter(_hq);
                float d = Vector2.Distance(pos, c);
                if (d < best) { best = d; bc = c; }
            }
            for (int x = 0; x < GRID_W; x++)
                for (int y = 0; y < GRID_H; y++)
                    if (_struct[x, y] is Cell s && s.Blocks)
                    {
                        var c = GridCenter(new Point(x, y));
                        float d = Vector2.Distance(pos, c);
                        if (d < best) { best = d; bc = c; }
                    }
            return bc;
        }

        void PlaceOrUpgrade()
        {
            if (_state != GameState.Playing) return;
            var g = MouseGrid();
            if (!InBounds(g)) return;

            if (g == _hq)
            {
                if (_sel == BuildingType.Wall) return;

                Cell? target = null; int bestCost = int.MaxValue;
                foreach (var it in _hqStack)
                    if (it.Type == _sel && it.NextCost < bestCost) { target = it; bestCost = it.NextCost; }
                if (target is Cell t && _gold >= t.NextCost)
                {
                    _gold -= t.NextCost; t.Level++; t.Spent += t.NextCost; t.NextCost *= 2; ApplyUpgrade(t); return;
                }

                if (_limits.ContainsKey(_sel) && _counts[_sel] >= _limits[_sel]) return;
                int cost = BaseCost(_sel);
                if (_gold < cost) return;

                var s = NewCell(_sel);
                _hqStack.Add(s);
                _gold -= cost;
                if (s.Blocks && _counts.ContainsKey(_sel)) _counts[_sel]++;
                RecomputeFlow();
                return;
            }

            var cur = _struct[g.X, g.Y];
            if (cur is Cell c)
            {
                if (c.Type != _sel) return;
                if (_gold < c.NextCost) return;
                _gold -= c.NextCost; c.Level++; c.Spent += c.NextCost; c.NextCost *= 2; ApplyUpgrade(c);
                return;
            }

            if (_sel == BuildingType.Miner && _tiles[g.X, g.Y] != 'O') return;
            if (_limits.ContainsKey(_sel) && _counts[_sel] >= _limits[_sel]) return;
            int cst = BaseCost(_sel);
            if (_gold < cst) return;

            var ns = NewCell(_sel);
            _struct[g.X, g.Y] = ns;
            _gold -= cst;
            if (ns.Blocks && _counts.ContainsKey(_sel)) _counts[_sel]++;
            if (ns.Blocks) RecomputeFlow();
        }

        void RemoveStructure()
        {
            var g = MouseGrid();
            if (!InBounds(g)) return;

            if (g == _hq)
            {
                if (_hqStack.Count > 0)
                {
                    var s = _hqStack[^1];
                    int refund = (int)(s.Spent * 0.5f);
                    _gold += refund;
                    if (s.Blocks && _counts.ContainsKey(s.Type))
                        _counts[s.Type] = Math.Max(0, _counts[s.Type] - 1);
                    _hqStack.RemoveAt(_hqStack.Count - 1);
                    RecomputeFlow();
                }
                return;
            }

            var cur = _struct[g.X, g.Y];
            if (cur is not Cell sc) return;
            int rf = (int)(sc.Spent * 0.5f);
            _gold += rf;
            if (sc.Blocks && _counts.ContainsKey(sc.Type))
                _counts[sc.Type] = Math.Max(0, _counts[sc.Type] - 1);
            _struct[g.X, g.Y] = null;
            if (sc.Blocks) RecomputeFlow();
        }

        Cell NewCell(BuildingType t)
        {
            int hp = t switch
            {
                BuildingType.Wall => 100,
                BuildingType.Turret => 50,
                BuildingType.Miner => 30,
                BuildingType.Tesla => 80,
                BuildingType.Healer => 45,
                _ => 40
            };
            var s = new Cell
            {
                Type = t,
                Hp = hp,
                MaxHp = hp,
                Blocks = true,
                Level = 1,
                NextCost = BaseCost(t) * 2,
                Spent = BaseCost(t),
                Cool = 0
            };
            InitBuildingStats(s);
            return s;
        }

        static int BaseCost(BuildingType t) => t switch
        {
            BuildingType.Wall => 5,
            BuildingType.Turret => 10,
            BuildingType.Miner => 20,
            BuildingType.Tesla => 30,
            BuildingType.Healer => 25,
            _ => 10
        };

        void InitBuildingStats(Cell s)
        {
            switch (s.Type)
            {
                case BuildingType.Turret: s.FireInterval = 1f; s.Range = 5f; s.Dmg = 12; break;
                case BuildingType.Miner: s.MineRate = 4f; break;
                case BuildingType.Tesla: s.FireInterval = 0.5f; s.Range = 3f; s.TeslaSlow = 0.60f; s.Dmg = 8; break;
                case BuildingType.Healer: s.HealRange = 3f; s.HealRate = 6f; s.SelfHeal = true; break;
            }
        }

        void ApplyUpgrade(Cell s)
        {
            switch (s.Type)
            {
                case BuildingType.Wall:
                    s.MaxHp = 100 + 100 * (s.Level - 1); s.Hp = s.MaxHp; break;
                case BuildingType.Turret:
                    s.FireInterval = 1f * MathF.Pow(0.85f, s.Level - 1);
                    s.Range = 5f + 0.7f * (s.Level - 1);
                    s.Dmg = 12 + 2 * (s.Level - 1); break;
                case BuildingType.Miner:
                    s.MineRate = 4f * MathF.Pow(1.5f, s.Level - 1); break;
                case BuildingType.Tesla:
                    s.TeslaSlow = MathF.Max(0.30f, 0.60f - 0.08f * (s.Level - 1));
                    s.FireInterval = 0.5f * MathF.Pow(0.92f, s.Level - 1);
                    s.Dmg = 8 + (s.Level - 1); break;
                case BuildingType.Healer:
                    s.HealRate = 6f * MathF.Pow(1.5f, s.Level - 1);
                    s.HealRange = 3f + 0.5f * (s.Level - 1); break;
            }
        }

        // ====== 座標系 ======
        void UpdateScaleAndOffsets()
        {
            var vp = GraphicsDevice.Viewport;
            int cw = Math.Max(640, vp.Width);
            int ch = Math.Max(360, vp.Height);
            float sx = cw / ((GRID_W + SIDEBAR_TILES) * (float)BASE_TILE);
            float sy = ch / (GRID_H * (float)BASE_TILE);
            _scale = MathF.Max(0.5f, MathF.Min(sx, sy));
            _sidebarPx = (int)(SIDEBAR_TILES * BASE_TILE * _scale);

            int totalW = (int)(GRID_W * BASE_TILE * _scale) + _sidebarPx;
            int totalH = (int)(GRID_H * BASE_TILE * _scale);
            _ox = (cw - totalW) / 2;
            _oy = (ch - totalH) / 2;
        }

        bool InBounds(Point g) => g.X >= 0 && g.X < GRID_W && g.Y >= 0 && g.Y < GRID_H;

        Point MouseGrid()
        {
            var p = _ms.Position;
            int TILE = (int)(BASE_TILE * _scale);
            int gx = (p.X - _ox) / TILE;
            int gy = (p.Y - _oy) / TILE;
            int gridWpx = GRID_W * TILE;
            if (p.X < _ox || p.Y < _oy) return new Point(-1, -1);
            if (p.X >= _ox + gridWpx || p.Y >= _oy + GRID_H * TILE) return new Point(-1, -1);
            return new Point(gx, gy);
        }

        Point GridAtScreen(Vector2 pos)
        {
            int gx = (int)(pos.X / BASE_TILE);
            int gy = (int)(pos.Y / BASE_TILE);
            return new Point(Math.Clamp(gx, 0, GRID_W - 1), Math.Clamp(gy, 0, GRID_H - 1));
        }

        Vector2 GridCenter(Point g) => new((g.X + 0.5f) * BASE_TILE, (g.Y + 0.5f) * BASE_TILE);
        Point Screen(Vector2 p) => new(_ox + (int)(p.X * _scale), _oy + (int)(p.Y * _scale));

        // サイドバーの建物ボタン描画
        void DrawBuildButton(int x, int y, BuildingType type, string label, float s)
        {
            bool selected = (_sel == type);
            Color bg = selected ? new Color(59, 130, 246) : new Color(31, 41, 55);
            FillRect(x, y, (int)(180 * s), (int)(60 * s), bg);
            DrawRect(x, y, (int)(180 * s), (int)(60 * s), Color.White, Math.Max(1, (int)(2 * s)));
            DrawText(x + (int)(12 * s), y + (int)(18 * s), label, Color.White, (int)(16 * s));
        }

        bool CanPlacePreview(Point g)
        {
            if (!InBounds(g)) return false;

            if (g == _hq)
            {
                if (_sel == BuildingType.Wall) return false;
                foreach (var it in _hqStack)
                    if (it.Type == _sel && _gold >= it.NextCost) return true;
                if (_limits.ContainsKey(_sel) && _counts[_sel] >= _limits[_sel]) return false;
                return _gold >= BaseCost(_sel);
            }

            var s = _struct[g.X, g.Y];
            if (s is null)
            {
                if (_sel == BuildingType.Miner && _tiles[g.X, g.Y] != 'O') return false;
                if (_limits.ContainsKey(_sel) && _counts[_sel] >= _limits[_sel]) return false;
                return _gold >= BaseCost(_sel);
            }
            else
            {
                if (s.Type != _sel) return false;
                return _gold >= s.NextCost;
            }
        }

        // ====== 入力ヘルパ ======
        bool Pressed(Keys k) => _ks.IsKeyDown(k) && !_pks.IsKeyDown(k);
        bool ClickedLeft() => _ms.LeftButton == ButtonState.Pressed && _pms.LeftButton == ButtonState.Released;
        bool ClickedRight() => _ms.RightButton == ButtonState.Pressed && _pms.RightButton == ButtonState.Released;
        bool MouseIn(Rectangle r) => r.Contains(_ms.Position);

        // ====== 描画ユーティリティ ======
        void FillRect(int x, int y, int w, int h, Color c) => _sb.Draw(_pix, new Rectangle(x, y, w, h), c);
        void DrawRect(int x, int y, int w, int h, Color c, int thickness = 2)
        {
            FillRect(x, y, w, thickness, c);
            FillRect(x, y + h - thickness, w, thickness, c);
            FillRect(x, y, thickness, h, c);
            FillRect(x + w - thickness, y, thickness, h, c);
        }
        void FillCircle(Point p, int r, Color c) => _sb.Draw(_pix, new Rectangle(p.X - r, p.Y - r, 2 * r, 2 * r), c);
        void DrawCircle(Point p, int r, Color c) => DrawRect(p.X - r, p.Y - r, 2 * r, 2 * r, c, 2);

        void DrawText(int x, int y, string text, Color c, int size, bool centered = false)
        {
            if (_font == null) return;
            float baseLine = _font.LineSpacing;
            float scale = size > 0 ? size / baseLine : 1f;
            var measured = _font.MeasureString(text) * scale;
            var pos = new Vector2(x, y);
            if (centered) pos -= measured / 2f;
            _sb.DrawString(_font, text, pos, c, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        // ★ 追加：建物の描画本体（CS0103対策）
        void DrawBuilding(int gx, int gy, Cell sc, int TILE, float s, int yOffset)
        {
            int X = _ox + gx * TILE;
            int Y = _oy + gy * TILE + yOffset;

            switch (sc.Type)
            {
                case BuildingType.Wall:
                    FillRect(X + (int)(6 * s), Y + (int)(6 * s), TILE - (int)(12 * s), TILE - (int)(12 * s), new Color(100, 116, 139));
                    break;
                case BuildingType.Turret:
                    FillCircle(new Point(X + TILE / 2, Y + TILE / 2), (int)((TILE / 2 - 8 * s)), new Color(192, 132, 252));
                    FillRect(X + (int)(18 * s), Y + (int)(4 * s), (int)(4 * s), (int)(8 * s), new Color(192, 132, 252));
                    break;
                case BuildingType.Miner:
                    FillRect(X + (int)(6 * s), Y + (int)(6 * s), TILE - (int)(12 * s), TILE - (int)(12 * s), new Color(34, 197, 94));
                    FillCircle(new Point(X + TILE / 2, Y + TILE / 2), (int)(TILE / 2 - 14 * s), new Color(20, 83, 45));
                    break;
                case BuildingType.Tesla:
                    FillRect(X + (int)(6 * s), Y + (int)(6 * s), TILE - (int)(12 * s), TILE - (int)(12 * s), new Color(56, 189, 248));
                    DrawRect(X + (int)(8 * s), Y + (int)(8 * s), TILE - (int)(16 * s), TILE - (int)(16 * s), new Color(224, 242, 254), Math.Max(1, (int)(2 * s)));
                    break;
                case BuildingType.Healer:
                    FillRect(X + (int)(6 * s), Y + (int)(6 * s), TILE - (int)(12 * s), TILE - (int)(12 * s), new Color(245, 158, 11));
                    // 回復範囲円
                    float rtiles = sc.HealRange;
                    float rpxf = rtiles * TILE;
                    int rpx = (int)rpxf;
                    var c = new Point(X + TILE / 2, Y + TILE / 2);
                    DrawCircle(c, rpx, new Color(253, 230, 138));
                    break;
            }

            // HPバーとLv
            int hpw = (int)((TILE - 8 * s) * (sc.Hp / MathF.Max(1, sc.MaxHp)));
            FillRect(X + (int)(4 * s), Y + TILE - (int)(6 * s), hpw, (int)(3 * s), new Color(34, 197, 94));
            // レベル表示
            DrawText(X + TILE / 2, Y + (int)(4 * s), $"Lv{sc.Level}", Color.White, (int)(14 * s), true);
            // レベル表示はフォント導入後に描画
        }

        void MenuLayout(out Rectangle playBtn, out Rectangle howBtn, float s)
        {
            int totalW = (int)(GRID_W * BASE_TILE * s) + _sidebarPx;
            int totalH = (int)(GRID_H * BASE_TILE * s);
            int cx = _ox + totalW / 2;
            int bw = (int)(280 * s);
            int bh = (int)(70 * s);
            int baseY = _oy + (int)(totalH * 0.50f);
            playBtn = new Rectangle(cx - bw / 2, baseY, bw, bh);
            howBtn = new Rectangle(cx - bw / 2, baseY + (int)(90 * s), bw, bh);
        }

        void HowToLayout(out Rectangle backBtn, out Rectangle playBtn, float s)
        {
            int totalW = (int)(GRID_W * BASE_TILE * s) + _sidebarPx;
            int totalH = (int)(GRID_H * BASE_TILE * s);
            int cx = _ox + totalW / 2;
            int bw = (int)(200 * s);
            int bh = (int)(60 * s);
            int baseY = _oy + (int)(totalH * 0.78f);
            backBtn = new Rectangle(cx - (int)(110 * s) - bw / 2, baseY, bw, bh);
            playBtn = new Rectangle(cx + (int)(110 * s) - bw / 2, baseY, bw, bh);
        }

        void DrawButton(Rectangle r, string label, bool hover, float s)
        {
            Color bg = hover ? new Color(59, 130, 246) : new Color(31, 41, 55);
            FillRect(r.X, r.Y, r.Width, r.Height, bg);
            DrawRect(r.X, r.Y, r.Width, r.Height, Color.White, Math.Max(2, (int)(2 * s)));
            DrawText(r.X + r.Width / 2, r.Y + r.Height / 2, label, Color.White, (int)(24 * s), true);
        }

        void DrawMainMenu(int totalW, int totalH, float s)
        {
            int cx = _ox + totalW / 2;
            int cy = _oy + (int)(totalH * 0.28f);
            DrawText(cx, cy, "Mini RTS Defense", Color.White, (int)(48 * s), true);
            DrawText(cx, cy + (int)(50 * s), "拠点を守りながら波状攻撃を凌ごう", Color.LightGray, (int)(16 * s), true);
            MenuLayout(out var playBtn, out var howBtn, s);
            bool hPlay = MouseIn(playBtn);
            bool hHow = MouseIn(howBtn);
            DrawButton(playBtn, "今すぐプレイ", hPlay, s);
            DrawButton(howBtn, "遊び方", hHow, s);

            MusicLayout(out var volOn, out var volHalf, out var volOff, s);
            DrawMusicButtons(volOn, volHalf, volOff, s);
        }

        void MusicLayout(out Rectangle volOn, out Rectangle volHalf, out Rectangle volOff, float s)
        {
            int totalW = (int)(GRID_W * BASE_TILE * s) + _sidebarPx;
            int totalH = (int)(GRID_H * BASE_TILE * s);
            // 左端に縦並びで配置して他ボタンと被らないようにする
            int leftX = _ox + (int)(24 * s);
            int topY = _oy + (int)(totalH * 0.28f) + (int)(80 * s); // タイトルの少し下
            int bw = (int)(170 * s);
            int bh = (int)(48 * s);
            int sp = (int)(10 * s);
            volOn = new Rectangle(leftX, topY, bw, bh);
            volHalf = new Rectangle(leftX, topY + bh + sp, bw, bh);
            volOff = new Rectangle(leftX, topY + (bh + sp) * 2, bw, bh);
            _btnVolOn = volOn; _btnVolHalf = volHalf; _btnVolOff = volOff;
        }

        void DrawMusicButtons(Rectangle volOn, Rectangle volHalf, Rectangle volOff, float s)
        {
            DrawMusicButton(volOn, "BGM: ON", _musicVol > 0.75f, MouseIn(volOn), s);
            DrawMusicButton(volHalf, "BGM: 半分", _musicVol > 0.25f && _musicVol <= 0.75f, MouseIn(volHalf), s);
            DrawMusicButton(volOff, "BGM: OFF", _musicVol <= 0.01f, MouseIn(volOff), s);
        }

        void DrawMusicButton(Rectangle r, string label, bool active, bool hover, float s)
        {
            Color bg;
            if (active) bg = new Color(59, 130, 246);
            else if (hover) bg = new Color(37, 99, 235);
            else bg = new Color(31, 41, 55);
            FillRect(r.X, r.Y, r.Width, r.Height, bg);
            DrawRect(r.X, r.Y, r.Width, r.Height, Color.White, Math.Max(2, (int)(2 * s)));
            DrawText(r.X + r.Width / 2, r.Y + r.Height / 2, label, Color.White, (int)(20 * s), true);
        }

        void DrawHowTo(int totalW, int totalH, float s)
        {
            int cx = _ox + totalW / 2;
            DrawText(cx, _oy + (int)(40 * s), "遊び方", Color.White, (int)(40 * s), true);
            string[] lines = new[]
            {
                "1. 1～5キーで建物種別を選択 (壁/砲台/採掘機/テスラ/回復塔)",
                "2. 左クリック: 配置 / 同じ建物を再クリックで強化", 
                "3. 右クリック: 建物を撤去 (コストの一部返却)",
                "4. 採掘機は鉱石(O)の上で金獲得 / 波ごとに敵が強化", 
                "5. テスラはチェイン攻撃&減速 / 回復塔は周囲と拠点を徐々に回復", 
                "6. 拠点HPが0で敗北。全ての敵を倒して次ウェーブへ", 
                "7. Pで一時停止 / Escでメニューに戻る"
            };
            for (int i = 0; i < lines.Length; i++)
                DrawText(cx, _oy + (int)(110 * s) + (int)(i * 28 * s), lines[i], Color.LightGray, (int)(16 * s), true);
            HowToLayout(out var backBtn, out var playBtn, s);
            DrawButton(backBtn, "メニューへ", MouseIn(backBtn), s);
            DrawButton(playBtn, "今すぐプレイ", MouseIn(playBtn), s);
        }
    }
}
