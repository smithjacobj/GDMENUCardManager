﻿//
// Copyright (c) 2008-2011, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

namespace DiscUtils.Iso9660
{
    using System.Text;

    internal sealed class RockRidgeExtension : SuspExtension
    {
        private string _variant;

        public RockRidgeExtension(string identifier)
        {
            _variant = identifier;
        }

        public override string Identifier
        {
            get { return _variant; }
        }

        public override SystemUseEntry Parse(string name, byte[] data, int offset, int length, Encoding encoding)
        {
            switch (name)
            {
                case "PX":
                    return new PosixFileInfoSystemUseEntry(data, offset);

                case "NM":
                    return new PosixNameSystemUseEntry(data, offset);

                case "CL":
                    return new ChildLinkSystemUseEntry(data, offset);

                case "TF":
                    return new FileTimeSystemUseEntry(data, offset);

                default:
                    return new GenericSystemUseEntry(data, offset);
            }
        }
    }
}
