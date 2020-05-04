using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository.Classifiers
{
    public class NuixPackageClassifier : IEntryClassifier
    {
        public IEnumerable<string> Classify(NuixLogEntry entry)
        {
            // Look for log entries that appear to be Nuix code (and not dependencies) based on them
            // being from package com.nuix.X
            if (entry.Source.ToLower().StartsWith("com.nuix"))
            {
                return new string[] { "nuix_package" };
            }
            else
            {
                return null;
            }
        }
    }
}
