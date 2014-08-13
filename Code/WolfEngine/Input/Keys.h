#ifndef _KEYS_H
#define _KEYS_H


///
/// All the keys as an integer, 0 = key up, 1 = key down, 2 = key released
///
typedef struct{
	int A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
	LeftShift, LeftControl, LeftAlt, Space, RightAlt, RightControl, RightShift,
	ArrowUp, ArrowLeft, ArrowDown, ArrowRight,
	Return, KeypadReturn, Tab;
}Keys;
#endif