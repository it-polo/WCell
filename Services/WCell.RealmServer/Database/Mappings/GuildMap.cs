﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentNHibernate;
using FluentNHibernate.Mapping;
using WCell.RealmServer.Database.Entities;

namespace WCell.RealmServer.Database.Mappings
{
	class GuildMap : ClassMap<Guild>
	{
		public GuildMap()
		{
			Id(x => x.Id).Not.Nullable();
			Id(Reveal.Member<Guild>("_leaderLowId")).Not.Nullable();
			Map(x => x.Created).Not.Nullable();
			Map(x => x.Info).Not.Nullable();
			Map(x => x.MOTD).Not.Nullable();
			Map(x => x.Name).Unique().Not.Nullable();
			References(x => x.Tabard);
		}
	}
}
