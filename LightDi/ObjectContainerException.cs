using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace LightDi
{
    [Serializable]
    public class ObjectContainerException : Exception
    {
        public ObjectContainerException(string message, Type[] resolutionPath) : base(GetMessage(message,
            resolutionPath))
        {
        }

        protected ObjectContainerException(
            SerializationInfo info,
            StreamingContext context) : base(info, context)
        {
        }
        private static string GetMessage(string message, IReadOnlyCollection<Type> resolutionPath)
        {
            if (resolutionPath == null || resolutionPath.Count == 0)
                return message;

            return
                $"{message} (resolution path: {string.Join("->", resolutionPath.Select(t => t.FullName).ToArray())})";
        }
    }

  
}