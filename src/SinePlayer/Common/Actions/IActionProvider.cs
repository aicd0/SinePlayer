// Copyright (c) aicd0. All rights reserved.
// Licensed under the GNU General Public License v3.0 or later.

using System.Collections.Specialized;
using System.Threading.Tasks;

namespace SinePlayer.Common.Actions;

internal interface IActionProvider
{
    string Name { get; }

    Task<ActionResult> Handle(IActionProviderContext context, NameValueCollection parameters);
}
