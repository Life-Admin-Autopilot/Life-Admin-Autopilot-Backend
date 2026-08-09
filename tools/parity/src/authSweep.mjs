// A synthetic scenario, derived directly from the contract, that hits EVERY
// authenticated operation with no Authorization header and expects 401.
//
// Why generate it instead of writing it out as data: it is mechanical, it is
// 70-odd steps of pure boilerplate, and deriving it from the contract means it
// can never drift from the operation inventory it is supposed to cover.
//
// Why it is worth having at all:
//   * forgetting to put one controller behind auth is a classic porting
//     mistake, and testing the middleware on two routes does not catch it;
//   * it costs ZERO auth rate-limit budget — none of these requests reach a
//     limiter that counts them against a session;
//   * on the routes declared `requireAuth, strictAuthLimiter` it pins the
//     documented middleware ORDER: authentication runs first, so an
//     unauthenticated call is rejected without consuming a strict slot.

const PLACEHOLDER_ID = '6a78c437aa461ae1dc64ffff';

export const AUTH_SWEEP_NAME = 'auth-sweep';

export function buildAuthSweepScenario(contract) {
  const steps = contract.operations
    .filter((operation) => operation.requiresAuth)
    .map((operation) => ({
      name: `unauthenticated-${operation.operationId}`,
      op: operation.operationId,
      method: operation.method,
      path: operation.pathTemplate.replace(/\{[A-Za-z0-9_]+\}/g, PLACEHOLDER_ID),
      auth: false,
      expect: { status: 401 },
    }));

  return {
    file: '(generated)',
    name: AUTH_SWEEP_NAME,
    description:
      'Every authenticated operation, called with no Authorization header. Generated from the contract; costs no rate-limit budget.',
    tags: ['auth', 'cheap', 'generated'],
    user: 'none',
    generated: true,
    skip: false,
    skipReason: '',
    steps,
  };
}
