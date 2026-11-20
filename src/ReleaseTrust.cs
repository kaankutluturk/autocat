// Public release verification key.
static class ReleaseTrust {
    internal const string PublicKeyXml = "<RSAKeyValue><Modulus>7rXqpg/CifQkVP4aco8zSnLHkv5VrDGP34A2W/iiN0ZQ6hxI+rwOACsrNCnIuFJuAHmfTQxDj6era8VjY+1PLMXQp0gopIsVfvBAbDG5yHxmheEfLYDQD/0VIlqQqj3zYiWY9P+8Z12i4YAZJscqdodQnd1f7M59trhiwl+jSanPv9r0gfrW7nsz9H+hm7omn0Y6mumanvtptfomkvg63LPzQ9erq2GGLGKQbSRstG6q/EsKGe3AjYQ3aF7KJfFeAS/G/+qTy3Y9JcEhIPF9G2mRPJ6bkQhFTAeUQu23BgYY5fi1+a+dYy1MH0yE+MjQpdZkvK/z9u4+pX5Grzu0HAVJElpX3xzy2RrbRIAm1nh9JDZUqkusPUj3Xeb4D3XyvxDUhJb6GMRDzrqQuITPjjrHe3vVgVmu8FHEsWhZQpC5pMDgJP6aOK5C627R8WSa+jfZKprVakKlZYRlEcvHe+CPKsmzxdGYcMCw44GUNhHZuz+6e8ma1JZ/tQJctk39</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
    internal static bool Ready { get { return PublicKeyXml.Length > 0; } }
}
