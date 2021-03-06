﻿using System; using ComponentBind;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Lemma.Components;

namespace Lemma.Factories
{
	public class CounterFactory : Factory<Main>
	{
		public CounterFactory()
		{
			this.Color = new Vector3(0.0f, 0.4f, 1.0f);
		}

		public override Entity Create(Main main)
		{
			return new Entity(main, "Counter");
		}

		public override void Bind(Entity entity, Main main, bool creating = false)
		{
			entity.CannotSuspend = true;
			entity.GetOrCreate<Transform>("Transform");
			Counter c = entity.GetOrCreate<Counter>("Counter");
			base.Bind(entity, main, creating);
			entity.Add("StartingValue", c.StartingValue);
			entity.Add("Target", c.Target);
			entity.Add("IncrementBy", c.IncrementBy);
			entity.Add("OnlyTriggerOnce", c.OnlyOnce);
			entity.Add("OnTargetHit", c.OnTargetHit);
			entity.Add("Increment", c.Increment);
			entity.Add("Reset", c.Reset);
			entity.Add("Value", c.Count, readOnly: true);
			entity.Add("HasHitTarget", c.HasHitTarget, readOnly: true);
		}

		public override void AttachEditorComponents(Entity entity, Main main)
		{
			base.AttachEditorComponents(entity, main);

			Scriptlike.AttachEditorComponents(entity, main, this.Color);
		}
	}
}
