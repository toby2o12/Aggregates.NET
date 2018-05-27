﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregates.Internal
{
    class Constants
    {
        public static readonly string GeneratedIdPrefix = ">>";
        public static readonly string GeneratedAnyId = $"{GeneratedIdPrefix}ANY<<";
        public static readonly Func<string, string> GenerateNamedId = (key) => $"{GeneratedIdPrefix}{key}<<";
        public static readonly Func<int, string> GeneratedNumberedId = (key) => $"{GeneratedIdPrefix}{key}<<";
    }
}
