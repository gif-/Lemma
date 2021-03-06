﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Xml.Serialization;
using ComponentBind;

namespace Lemma.Components
{
	public class Water : Component<Main>, IDrawableAlphaComponent, IDrawablePreFrameComponent, IUpdateableComponent
	{
		private static List<Water> instances = new List<Water>();

		public static IEnumerable<Water> ActiveInstances
		{
			get
			{
				return instances.Where(x => !x.Suspended);
			}
		}

		/// <summary>
		/// A struct that represents a single vertex in the
		/// vertex buffer.
		/// </summary>
		private struct QuadVertex : IVertexType
		{
			public Vector3 Position;
			public Vector2 TexCoord;
			public Vector3 Normal;
			public VertexDeclaration VertexDeclaration
			{
				get
				{
					return Water.VertexDeclaration;
				}
			}
		}

		public Property<int> DrawOrder { get; set; }

		private VertexBuffer surfaceVertexBuffer;
		private VertexBuffer underwaterVertexBuffer;

		private static VertexDeclaration vertexDeclaration;
		public static VertexDeclaration VertexDeclaration
		{
			get
			{
				if (Water.vertexDeclaration == null)
				{
					Microsoft.Xna.Framework.Graphics.VertexElement[] declElements = new VertexElement[3];
					declElements[0].Offset = 0;
					declElements[0].UsageIndex = 0;
					declElements[0].VertexElementFormat = VertexElementFormat.Vector3;
					declElements[0].VertexElementUsage = VertexElementUsage.Position;
					declElements[1].Offset = sizeof(float) * 3;
					declElements[1].UsageIndex = 0;
					declElements[1].VertexElementFormat = VertexElementFormat.Vector2;
					declElements[1].VertexElementUsage = VertexElementUsage.TextureCoordinate;
					declElements[2].Offset = sizeof(float) * 5;
					declElements[2].UsageIndex = 0;
					declElements[2].VertexElementFormat = VertexElementFormat.Vector3;
					declElements[2].VertexElementUsage = VertexElementUsage.Normal;
					Water.vertexDeclaration = new VertexDeclaration(declElements);
				}
				return Water.vertexDeclaration;
			}
		}

		public static Property<bool> BigWaterShader = new Property<bool>();
		public static Property<float> BigWaterHeight = new Property<float>();
		public Property<Vector3> Position = new Property<Vector3>();
		public Property<Vector3> Color = new Property<Vector3> { Value = new Vector3(0.7f, 0.9f, 1.0f) };
		public Property<Vector3> UnderwaterColor = new Property<Vector3> { Value = new Vector3(0.0f, 0.07f, 0.13f) };
		public Property<float> Fresnel = new Property<float> { Value = 0.6f };
		public Property<float> Speed = new Property<float> { Value = 0.075f };
		public Property<float> RippleDensity = new Property<float> { Value = 1.0f };
		public Property<bool> EnableReflection = new Property<bool> { Value = true };
		public Property<float> Distortion = new Property<float> { Value = 0.25f };
		public Property<float> Brightness = new Property<float> { Value = 0.1f };
		public Property<float> Clearness = new Property<float> { Value = 0.25f };
		public Property<float> Depth = new Property<float> { Value = 100.0f };
		public Property<float> Refraction = new Property<float> { Value = 0.0f };
		public Property<Vector2> Scale = new Property<Vector2> { Value = new Vector2(100.0f, 100.0f) };
		public Property<bool> CannotSuspendByDistance = new Property<bool>();

		public static Water Get(Vector3 pos)
		{
			for (int i = 0; i < Water.instances.Count; i++)
			{
				Water water = Water.instances[i];
				if (water.Fluid.BoundingBox.Contains(pos) != ContainmentType.Disjoint)
					return water;
			}
			return null;
		}

		public static Water Raycast(Vector3 pos, Vector3 ray, float radius, out Vector3 hit)
		{
			hit = Vector3.Zero;
			if (ray.Y == 0)
				return null;
			for (int i = 0; i < Water.instances.Count; i++)
			{
				Water water = Water.instances[i];
				float height = water.Position.Value.Y;
				float distance = (height - pos.Y) / ray.Y;
				if (distance > 0 && distance < radius)
				{
					Vector3 p = pos + (ray * distance);
					BoundingBox b = water.Fluid.BoundingBox;
					if (p.X > b.Min.X && p.Z > b.Min.Z
						&& p.Z < b.Max.X && p.Z < b.Max.Z)
					{
						hit = p;
						return water;
					}
				}
			}
			return null;
		}

		private Renderer renderer;
		private RenderTarget2D buffer;
		private Effect effect;
		private RenderParameters parameters;
		private Camera camera;

		private bool underwater;

		[XmlIgnore]
		public Util.CustomFluidVolume Fluid;

		private bool needResize = false;

		private static Random random = new Random();

		public Water()
		{
			this.DrawOrder = new Property<int> { Value = 10 };
		}

		private const float resolutionRatio = 0.25f;

		private void resize()
		{
			Point size = this.main.ScreenSize;
			size.X = (int)((float)size.X * Water.resolutionRatio);
			size.Y = (int)((float)size.Y * Water.resolutionRatio);
			if (this.renderer == null)
			{
				this.renderer = new Renderer(this.main, false, false, false, false, false);
				this.renderer.MotionBlurAmount.Value = 0.0f;
				this.renderer.LightRampTexture.Value = "LightRamps\\default";
				this.main.AddComponent(this.renderer);
				this.renderer.ReallocateBuffers(size);
			}
			else
				this.renderer.ReallocateBuffers(size);

			if (this.buffer != null)
				this.buffer.Dispose();
			this.buffer = new RenderTarget2D(this.main.GraphicsDevice, size.X, size.Y);

			this.needResize = false;
		}

		private void loadEffectAndVertexBuffers()
		{
			this.effect = this.main.Content.Load<Effect>("Effects\\Water").Clone();
			this.effect.Parameters["NormalMap" + Model.SamplerPostfix].SetValue(this.main.Content.Load<Texture2D>("Textures\\water-normal"));

			this.Color.Reset();
			this.Scale.Reset();
			this.Fresnel.Reset();
			this.Speed.Reset();
			this.RippleDensity.Reset();
			this.Distortion.Reset();
			this.Brightness.Reset();
			this.Refraction.Reset();
			this.UnderwaterColor.Reset();
			
			// Can't just call this.Position.Reset() because it will try to write to the Water.BigWaterHeight property
			// which sets a skybox effect parameter. The skybox effect might not have been loaded yet.
			this.effect.Parameters["Position"].SetValue(this.Position);

			// Surface
			this.surfaceVertexBuffer = new VertexBuffer(this.main.GraphicsDevice, typeof(QuadVertex), Water.VertexDeclaration.VertexStride * 4, BufferUsage.None);
			QuadVertex[] surfaceData = new QuadVertex[4];

			// Upper right
			const float scale = 0.5f;
			surfaceData[0].Position = new Vector3(scale, 0, scale);
			surfaceData[0].TexCoord = new Vector2(1, 0);

			// Upper left
			surfaceData[1].Position = new Vector3(-scale, 0, scale);
			surfaceData[1].TexCoord = new Vector2(0, 0);

			// Lower right
			surfaceData[2].Position = new Vector3(scale, 0, -scale);
			surfaceData[2].TexCoord = new Vector2(1, 1);

			// Lower left
			surfaceData[3].Position = new Vector3(-scale, 0, -scale);
			surfaceData[3].TexCoord = new Vector2(0, 1);

			surfaceData[0].Normal = surfaceData[1].Normal = surfaceData[2].Normal = surfaceData[3].Normal = new Vector3(0, 1, 0);

			this.surfaceVertexBuffer.SetData(surfaceData);

			// Underwater
			this.underwaterVertexBuffer = new VertexBuffer(this.main.GraphicsDevice, typeof(QuadVertex), Water.VertexDeclaration.VertexStride * 4, BufferUsage.None);

			QuadVertex[] underwaterData = new QuadVertex[4];

			// Upper right
			underwaterData[0].Position = new Vector3(1, 1, 1);
			underwaterData[0].TexCoord = new Vector2(1, 0);

			// Lower right
			underwaterData[1].Position = new Vector3(1, -1, 1);
			underwaterData[1].TexCoord = new Vector2(1, 1);

			// Upper left
			underwaterData[2].Position = new Vector3(-1, 1, 1);
			underwaterData[2].TexCoord = new Vector2(0, 0);

			// Lower left
			underwaterData[3].Position = new Vector3(-1, -1, 1);
			underwaterData[3].TexCoord = new Vector2(0, 1);

			underwaterData[0].Normal = underwaterData[1].Normal = underwaterData[2].Normal = underwaterData[3].Normal = new Vector3(0, 0, -1);

			this.underwaterVertexBuffer.SetData(underwaterData);
		}

		public void LoadContent(bool reload)
		{
			this.loadEffectAndVertexBuffers();
			this.resize();
		}

		private Property<Vector3> soundPosition = new Property<Vector3>();

		public override void Awake()
		{
			base.Awake();
			this.EnabledWhenPaused = true;

			this.Add(new NotifyBinding(main.AlphaDrawablesModified, this.DrawOrder));

			this.Add(new SetBinding<bool>(this.CannotSuspendByDistance, delegate(bool value)
			{
				this.Entity.CannotSuspendByDistance = value;
			}));

			this.Add(new NotifyBinding(delegate() { this.needResize = true; }, this.main.ScreenSize));

			Action removeFluid = delegate()
			{
				if (this.Fluid.Space != null)
					this.main.Space.Remove(this.Fluid);
				AkSoundEngine.PostEvent(AK.EVENTS.STOP_WATER_LOOP, this.Entity);
			};

			Action addFluid = delegate()
			{
				if (this.Fluid.Space == null && this.Enabled && !this.Suspended)
				{
					this.main.Space.Add(this.Fluid);
					if (!this.main.EditorEnabled)
						AkSoundEngine.PostEvent(AK.EVENTS.PLAY_WATER_LOOP, this.Entity);
				}
			};

			this.Add(new CommandBinding(this.OnSuspended, removeFluid));
			this.Add(new CommandBinding(this.Disable, removeFluid));
			this.Add(new CommandBinding(this.OnResumed, addFluid));
			this.Add(new CommandBinding(this.Enable, addFluid));

			this.camera = new Camera();
			this.main.AddComponent(this.camera);
			this.parameters = new RenderParameters
			{
				Camera = this.camera,
				Technique = Technique.Clip,
				ReverseCullOrder = true,
			};

			this.Add(new SetBinding<Vector3>(this.Color, delegate(Vector3 value)
			{
				this.effect.Parameters["Color"].SetValue(value);
			}));

			this.Add(new SetBinding<Vector2>(this.Scale, delegate(Vector2 value)
			{
				this.effect.Parameters["Scale"].SetValue(value);
				this.updatePhysics();
			}));

			this.Add(new SetBinding<Vector3>(this.UnderwaterColor, delegate(Vector3 value)
			{
				this.effect.Parameters["UnderwaterColor"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.Fresnel, delegate(float value)
			{
				this.effect.Parameters["Fresnel"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.Speed, delegate(float value)
			{
				this.effect.Parameters["Speed"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.RippleDensity, delegate(float value)
			{
				this.effect.Parameters["RippleDensity"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.Distortion, delegate(float value)
			{
				this.effect.Parameters["Distortion"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.Brightness, delegate(float value)
			{
				this.effect.Parameters["Brightness"].SetValue(value);
			}));

			this.Add(new SetBinding<float>(this.Refraction, delegate(float value)
			{
				this.effect.Parameters["Refraction"].SetValue(value);
			}));

			this.Add(new SetBinding<Vector3>(this.Position, delegate(Vector3 value)
			{
				this.effect.Parameters["Position"].SetValue(this.Position);
				if (this.CannotSuspendByDistance)
					Water.BigWaterHeight.Value = value.Y;
				this.updatePhysics();
			}));

			this.Add(new SetBinding<float>(this.Depth, delegate(float value)
			{
				 this.updatePhysics();
			}));

			instances.Add(this);

			this.Add(new Binding<Vector3>(this.soundPosition,
			delegate(Vector3 pos)
			{
				BoundingBox box = this.Fluid.BoundingBox;
				pos.X = Math.Max(box.Min.X, Math.Min(pos.X, box.Max.X));
				pos.Y = this.Position.Value.Y;
				pos.Z = Math.Max(box.Min.Z, Math.Min(pos.Z, box.Max.Z));
				return pos;
			}, this.main.Camera.Position));
			Sound.AttachTracker(this.Entity, this.soundPosition);

			if (!this.main.EditorEnabled && this.Enabled && !this.Suspended)
				AkSoundEngine.PostEvent(AK.EVENTS.PLAY_WATER_LOOP, this.Entity);
		}

		private void updatePhysics()
		{
			if (this.Fluid != null)
				this.main.Space.Remove(this.Fluid);

			List<Vector3[]> tris = new List<Vector3[]>();
			float width = this.Scale.Value.X;
			float length = this.Scale.Value.Y;
			Vector3 pos = this.Position;

			tris.Add(new[]
			{
				pos + new Vector3(width / -2, 0, length / -2),
				pos + new Vector3(width / 2, 0, length / -2),
				pos + new Vector3(width / -2, 0, length / 2)
			});
			tris.Add(new[]
			{
				pos + new Vector3(width / -2, 0, length / 2),
				pos + new Vector3(width / 2, 0, length / -2),
				pos + new Vector3(width / 2, 0, length / 2)
			});

			this.Fluid = new Util.CustomFluidVolume(Vector3.Up, this.main.Space.ForceUpdater.Gravity.Y, tris, this.Depth, 0.8f, 0.997f, 0.2f, this.main.Space.BroadPhase.QueryAccelerator, this.main.Space.ThreadManager);
			this.main.Space.Add(this.Fluid);
		}

		private bool isVisible(Camera c)
		{
			Vector3 cameraPos = c.Position;
			bool underwater = this.Fluid.BoundingBox.Contains(cameraPos) != ContainmentType.Disjoint;
			if (!underwater && cameraPos.Y < this.Position.Value.Y)
				return false;
			
			if (!c.BoundingFrustum.Intersects(this.Fluid.BoundingBox))
				return false;
			
			return true;
		}

		void IDrawableAlphaComponent.DrawAlpha(Microsoft.Xna.Framework.GameTime time, RenderParameters p)
		{
			if (!p.IsMainRender || !this.isVisible(p.Camera))
				return;

			Vector3 cameraPos = p.Camera.Position;
			Vector3 pos = this.Position;

			RasterizerState originalState = this.main.GraphicsDevice.RasterizerState;
			this.main.GraphicsDevice.RasterizerState = new RasterizerState { CullMode = CullMode.None };

			float oldFarPlane = p.Camera.FarPlaneDistance;
			p.Camera.FarPlaneDistance.Value = 1000.0f;

			p.Camera.SetParameters(this.effect);
			this.effect.Parameters["ActualFarPlaneDistance"].SetValue(oldFarPlane);
			this.effect.Parameters["Reflection" + Model.SamplerPostfix].SetValue(this.buffer);
			this.effect.Parameters["Time"].SetValue(this.main.TotalTime);
			this.effect.Parameters["Depth" + Model.SamplerPostfix].SetValue(p.DepthBuffer);
			this.effect.Parameters["Frame" + Model.SamplerPostfix].SetValue(p.FrameBuffer);
			this.effect.Parameters["CameraPosition"].SetValue(cameraPos);

			bool reflection = this.EnableReflection && this.main.Settings.Reflections;

			// Draw surface
			this.effect.Parameters["Clearness"].SetValue(this.underwater ? 1.0f : this.Clearness);
			this.effect.CurrentTechnique = this.effect.Techniques[this.underwater || !reflection ? "Surface" : "SurfaceReflection"];
			this.effect.CurrentTechnique.Passes[0].Apply();

			if (this.surfaceVertexBuffer.IsDisposed)
				this.loadEffectAndVertexBuffers();

			this.main.GraphicsDevice.SetVertexBuffer(this.surfaceVertexBuffer);
			this.main.GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
			Model.DrawCallCounter++;
			Model.TriangleCounter += 2;

			this.main.GraphicsDevice.RasterizerState = originalState;

			p.Camera.FarPlaneDistance.Value = oldFarPlane;

			if (this.underwater)
			{
				// Draw underwater stuff
				this.effect.Parameters["Clearness"].SetValue(this.Clearness);
				this.effect.CurrentTechnique = this.effect.Techniques["Underwater"];

				// Ugh
				p.Camera.Position.Value = Vector3.Zero;
				p.Camera.SetParameters(this.effect);
				p.Camera.Position.Value = cameraPos;

				this.effect.CurrentTechnique.Passes[0].Apply();

				if (this.surfaceVertexBuffer.IsDisposed)
					this.loadEffectAndVertexBuffers();
				this.main.GraphicsDevice.SetVertexBuffer(this.underwaterVertexBuffer);
				this.main.GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleStrip, 0, 2);
				Model.DrawCallCounter++;
				Model.TriangleCounter += 2;
			}
		}

		void IDrawablePreFrameComponent.DrawPreFrame(GameTime time, RenderParameters p)
		{
			bool reflection = this.EnableReflection && this.main.Settings.Reflections;

			if (!reflection || !this.isVisible(p.Camera))
				return;

			if (this.needResize)
				this.resize();

			float waterHeight = this.Position.Value.Y;
			if (p.Camera.Position.Value.Y > waterHeight)
			{
				this.parameters.ClipPlanes = new[] { new Plane(Vector3.Up, -waterHeight) };
				Matrix reflect = Matrix.CreateTranslation(0.0f, -waterHeight, 0.0f) * Matrix.CreateScale(1.0f, -1.0f, 1.0f) * Matrix.CreateTranslation(0.0f, waterHeight, 0.0f);
				this.camera.Position.Value = Vector3.Transform(p.Camera.Position, reflect);
				this.camera.View.Value = reflect * p.Camera.View;
				this.camera.SetProjectionFromCamera(p.Camera);
				this.renderer.SetRenderTargets(this.parameters);

				this.main.DrawScene(this.parameters);

				this.renderer.PostProcess(this.buffer, this.parameters);
			}
		}

		private Dictionary<BEPUphysics.BroadPhaseEntries.MobileCollidables.EntityCollidable, float> submerged = new Dictionary<BEPUphysics.BroadPhaseEntries.MobileCollidables.EntityCollidable, float>();

		private const float speedMassVolumeCoefficient = 1.0f / 50.0f;

		private List<KeyValuePair<BEPUphysics.BroadPhaseEntries.MobileCollidables.EntityCollidable, float>> submergedCache = new List<KeyValuePair<BEPUphysics.BroadPhaseEntries.MobileCollidables.EntityCollidable, float>>();
		void IUpdateableComponent.Update(float dt)
		{
			if (this.main.Paused)
				return;

			bool newUnderwater = this.Fluid.BoundingBox.Contains(this.main.Camera.Position) != ContainmentType.Disjoint;
			if (newUnderwater != this.underwater)
				AkSoundEngine.SetState(AK.STATES.WATER.GROUP, newUnderwater ? AK.STATES.WATER.STATE.UNDERWATER : AK.STATES.WATER.STATE.NORMAL);
			this.underwater = newUnderwater;

			int drawOrder = this.CannotSuspendByDistance && newUnderwater ? 10 : -15;
			if (this.DrawOrder != drawOrder)
				this.DrawOrder.Value = drawOrder;

			Water.BigWaterShader.Value = this.CannotSuspendByDistance && !newUnderwater;

			float waterHeight = this.Position.Value.Y;

			float time = this.main.TotalTime;

			lock (this.Fluid.NotifyEntries)
			{
				foreach (BEPUphysics.BroadPhaseEntries.MobileCollidables.EntityCollidable collidable in this.Fluid.NotifyEntries)
				{
					if (collidable.Entity == null)
						continue;

					float speed = collidable.Entity.LinearVelocity.Length();

					if (speed > 9.0f)
					{
						float volume = Math.Min(speed * collidable.Entity.Mass * speedMassVolumeCoefficient, 1.0f);
						if (volume > 0.1f && !this.submerged.ContainsKey(collidable))
						{
							uint temp = Sound.RegisterTemp(collidable.Entity.Position);
							if (collidable.Entity.Mass > 40.0f)
								AkSoundEngine.PostEvent(AK.EVENTS.PLAY_WATER_SPLASH_HEAVY, temp);
							else
								AkSoundEngine.PostEvent(AK.EVENTS.PLAY_WATER_SPLASH, temp);
							AkSoundEngine.SetRTPCValue(AK.GAME_PARAMETERS.SFX_WATER_SPLASH_VOLUME, volume, temp);
							AkSoundEngine.UnregisterTemp(temp);
						}
					}

					if (speed > 5.0f)
					{
						collidable.UpdateBoundingBox();
						Water.SplashParticles(this.main, collidable.BoundingBox, collidable.Tag as Voxel, waterHeight);
					}

					this.submerged[collidable] = time;
				}
				this.Fluid.NotifyEntries.Clear();
			}

			this.submergedCache.AddRange(this.submerged);
			for (int i = 0; i < this.submergedCache.Count; i++)
			{
				var p = this.submergedCache[i];
				if (time - p.Value > 0.1f)
				{
					if (p.Key.Entity != null)
					{
						float speed = p.Key.Entity.LinearVelocity.Y;
						if (speed > 2.0f)
						{
							float volume = Math.Min(speed * p.Key.Entity.Mass * speedMassVolumeCoefficient, 1.0f);
							if (volume > 0.1f)
							{
								uint temp = Sound.RegisterTemp(p.Key.Entity.Position);
								AkSoundEngine.PostEvent(AK.EVENTS.PLAY_WATER_SPLASH_OUT, temp);
								AkSoundEngine.SetRTPCValue(AK.GAME_PARAMETERS.SFX_WATER_SPLASH_VOLUME, volume, temp);
								AkSoundEngine.UnregisterTemp(temp);
							}
						}
					}
					this.submerged.Remove(p.Key);
				}
			}
			this.submergedCache.Clear();
		}

		public static void SplashParticles(Main main, Vector3 pos, float radius)
		{
			float radius_squared = radius * radius;
			Vector3[] particlePositions = new Vector3[5 * (int)radius_squared];

			Vector3 min = pos + new Vector3(-radius, 0, -radius);

			int particleIndex = 0;
			for (int i = 0; i < particlePositions.Length; i++)
			{
				Vector3 particle = particlePositions[particleIndex] = new Vector3
				(
					min.X + ((float)random.NextDouble() * 2.0f * radius),
					pos.Y,
					min.Z + ((float)random.NextDouble() * 2.0f * radius)
				);
				if ((particle - pos).LengthSquared() < radius_squared)
					particleIndex++;
			}

			ParticleEmitter.Emit(main, "Splash", particlePositions.Take(particleIndex));

			ParticleEmitter.Emit(main, "BigSplash", particlePositions.Take(particleIndex / 5));
		}

		public static void SplashParticles(Main main, BoundingBox boundingBox, Voxel v, float waterHeight)
		{
			Vector3 diff = boundingBox.Max - boundingBox.Min;
			Vector3[] particlePositions = new Vector3[5 * (int)(diff.X * diff.Z)];

			int particleIndex = 0;
			for (int i = 0; i < particlePositions.Length; i++)
			{
				Vector3 pos = particlePositions[particleIndex] = new Vector3
				(
					boundingBox.Min.X + ((float)random.NextDouble() * diff.X),
					waterHeight,
					boundingBox.Min.Z + ((float)random.NextDouble() * diff.Z)
				);
				if (v == null || v[pos] != Voxel.States.Empty)
					particleIndex++;
			}

			ParticleEmitter.Emit(main, "Splash", particlePositions.Take(particleIndex));

			ParticleEmitter.Emit(main, "BigSplash", particlePositions.Take(particleIndex / 5));
		}

		public override void delete()
		{
			this.camera.Delete.Execute();
			this.effect.Dispose();
			this.renderer.Delete.Execute();
			this.buffer.Dispose();
			this.surfaceVertexBuffer.Dispose();
			this.underwaterVertexBuffer.Dispose();
			if (this.Fluid.Space != null)
				this.main.Space.Remove(this.Fluid);
			instances.Remove(this);
			base.delete();
			AkSoundEngine.PostEvent(AK.EVENTS.STOP_WATER_LOOP, this.Entity);
			Water.BigWaterShader.Value = false;
		}
	}
}